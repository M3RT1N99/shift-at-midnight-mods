using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Il2Cpp;
using Il2CppFusion;
using Il2CppFusion.Sockets;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OsamaBinLaden.Multiplayer
{
    /// <summary>
    /// Narrow adapter over the game's existing Fusion runner. The host can transmit only
    /// to peers that first sent a valid protocol frame; vanilla clients are never probed.
    /// </summary>
    internal sealed class FusionTransport : IDisposable
    {
        private const int MaximumFrameBytes = 2048;

        private readonly HashSet<int> _confirmedPeers = new HashSet<int>();
        private float _nextHookAttempt;
        private bool _warnedTransferMode;
        private bool _disposed;

        public event Action<int, ArraySegment<byte>> FrameReceived;

        public bool RunnerLive => GetRunner() != null;
        public bool ReceiveReady => ReceiveHook.Applied;
        public bool Available => RunnerLive && ReceiveReady;

        public bool IsHost
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner != null && runner.IsServer;
            }
        }

        public bool IsSolo
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner != null && runner.IsSinglePlayer;
            }
        }

        public int LocalPlayerId
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner == null ? -1 : runner.LocalPlayer.PlayerId;
            }
        }

        public int CurrentTick
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner == null ? 0 : runner.Tick.Raw;
            }
        }

        public int TickRate
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner == null ? 0 : Math.Max(1, runner.TickRate);
            }
        }

        public int RunnerInstanceId
        {
            get
            {
                NetworkRunner runner = GetRunner();
                return runner == null ? 0 : runner.GetInstanceID();
            }
        }

        /// <summary>
        /// Host advertises protocol support in the lobby before any custom reliable frame
        /// is sent. Clients therefore never probe an unmodded host.
        /// </summary>
        public bool PublishHostMarker()
        {
            if (_disposed || !IsHost || IsSolo) return false;
            try
            {
                NetworkRunner runner = GetRunner();
                object sessionInfo = runner == null ? null : ReadMember(runner, "SessionInfo");
                if (sessionInfo == null || !Convert.ToBoolean(ReadMember(sessionInfo, "IsValid")))
                    return false;

                MethodInfo update = sessionInfo.GetType().GetMethods(
                        BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        method.Name == "UpdateCustomProperties" &&
                        method.GetParameters().Length == 1);
                if (update == null) return false;

                Type dictionaryType = update.GetParameters()[0].ParameterType;
                object properties = Activator.CreateInstance(dictionaryType);
                MethodInfo add = dictionaryType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        method.Name == "Add" && method.GetParameters().Length == 2);
                if (properties == null || add == null) return false;
                add.Invoke(properties, new object[]
                {
                    "obln", (SessionProperty)(int)EncounterProtocol.Version
                });
                return Convert.ToBoolean(update.Invoke(sessionInfo, new[] { properties }));
            }
            catch (Exception ex)
            {
                Log.Debug("lobby marker publish failed: " + ex.Message);
                return false;
            }
        }

        public bool HostMarkerPresent()
        {
            NetworkRunner runner = GetRunner();
            if (runner == null || runner.IsSinglePlayer) return false;
            if (runner.IsServer) return true;

            try
            {
                object sessionInfo = ReadMember(runner, "SessionInfo");
                object properties = ReadMember(sessionInfo, "Properties");
                if (properties == null) return false;
                MethodInfo containsKey = properties.GetType().GetMethod(
                    "ContainsKey", BindingFlags.Public | BindingFlags.Instance);
                PropertyInfo item = properties.GetType().GetProperty(
                    "Item", BindingFlags.Public | BindingFlags.Instance);
                if (containsKey == null || item == null ||
                    !Convert.ToBoolean(containsKey.Invoke(properties, new object[] { "obln" })))
                    return false;
                SessionProperty marker = item.GetValue(properties, new object[] { "obln" }) as SessionProperty;
                return marker != null && marker.IsInt && (int)marker == EncounterProtocol.Version;
            }
            catch { return false; }
        }

        public void Update(float realtime)
        {
            if (_disposed || ReceiveHook.Applied || realtime < _nextHookAttempt) return;

            NetworkRunner runner = GetRunner();
            if (runner == null || runner.IsSinglePlayer) return;

            _nextHookAttempt = realtime + 5f;
            ReceiveHook.Apply(this);
        }

        public int[] ActivePlayerIds()
        {
            NetworkRunner runner = GetRunner();
            if (runner == null) return Array.Empty<int>();

            var result = new List<int>();
            foreach (object value in Enumerate(runner.ActivePlayers))
            {
                if (!TryPlayerRef(value, out PlayerRef player)) continue;
                int id = player.PlayerId;
                if (id >= 0 && !result.Contains(id)) result.Add(id);
            }
            return result.ToArray();
        }

        public bool IsActivePlayer(int playerId)
        {
            if (playerId < 0) return false;
            int[] active = ActivePlayerIds();
            for (int index = 0; index < active.Length; index++)
                if (active[index] == playerId) return true;
            return false;
        }

        public bool TryResolvePlayer(
            int playerId,
            out PlayerManager playerManager,
            out Transform target)
        {
            playerManager = null;
            target = null;

            try
            {
                if (!TryGetPlayerRef(playerId, out PlayerRef playerRef)) return false;

                if (playerId == LocalPlayerId)
                {
                    ClientPlayer client = ClientPlayer.Instance;
                    if (client != null && client && client.playerMan != null && client.playerMan)
                        playerManager = client.playerMan;
                }

                FusionNetworkManager network = FusionNetworkManager.Instance;
                if (playerManager == null && network != null && network)
                {
                    NetworkObject playerObject = network.GetPlayer(playerRef);
                    if (playerObject != null && playerObject)
                    {
                        playerManager = playerObject.GetComponent<PlayerManager>();
                        if (playerManager == null)
                            playerManager = playerObject.GetComponentInChildren<PlayerManager>(true);
                    }
                }

                if (playerManager == null)
                {
                    StoreManager store = StoreManager.Instance;
                    var players = store?.playerMans;
                    if (players != null)
                    {
                        for (int index = 0; index < players.Count; index++)
                        {
                            PlayerManager candidate = players[index];
                            if (candidate == null || !candidate || candidate.Object == null ||
                                !candidate.Object)
                                continue;
                            if (candidate.Object.InputAuthority.PlayerId != playerId) continue;
                            playerManager = candidate;
                            break;
                        }
                    }
                }

                if (playerManager == null || !playerManager || playerManager.dead ||
                    !playerManager.gameObject.activeInHierarchy)
                {
                    playerManager = null;
                    return false;
                }

                target = playerManager.charController != null && playerManager.charController
                    ? playerManager.charController.transform
                    : playerManager.transform;
                if (target == null || !target)
                {
                    playerManager = null;
                    target = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("player resolution failed: " + ex.Message);
                playerManager = null;
                target = null;
                return false;
            }
        }

        public bool TryGetPlayerIdentity(
            int playerId,
            out int rawEncoded,
            out uint networkObjectId,
            out PlayerManager playerManager,
            out Transform target)
        {
            rawEncoded = 0;
            networkObjectId = 0;
            playerManager = null;
            target = null;

            NetworkRunner runner = GetRunner();
            if (runner == null || !runner.IsServer ||
                !TryGetPlayerRef(playerId, out PlayerRef playerRef) ||
                !playerRef.IsRealPlayer || !runner.IsPlayerValid(playerRef) ||
                !runner.IsPlayerCommitted(playerRef))
                return false;

            NetworkObject playerObject = runner.GetPlayerObject(playerRef);
            if (playerObject == null || !playerObject || !playerObject.IsValid ||
                !playerObject.HasStateAuthority || playerObject.Runner != runner ||
                playerObject.InputAuthority != playerRef)
                return false;

            if (!TryResolvePlayer(playerId, out playerManager, out target) ||
                playerManager.Object == null || !playerManager.Object ||
                playerManager.Object != playerObject || !playerManager.HasStateAuthority)
                return false;

            rawEncoded = playerRef.RawEncoded;
            networkObjectId = playerObject.Id.Raw;
            return rawEncoded > 0 && networkObjectId != 0;
        }

        public bool RevalidateHostDamageTarget(
            int rawEncoded,
            uint networkObjectId,
            out int playerId,
            out PlayerManager playerManager,
            out Transform target)
        {
            playerId = -1;
            playerManager = null;
            target = null;

            try
            {
                NetworkRunner runner = GetRunner();
                if (runner == null || !runner.IsServer || runner.IsSinglePlayer) return false;
                PlayerRef playerRef = PlayerRef.FromEncoded(rawEncoded);
                if (!playerRef.IsRealPlayer || !runner.IsPlayerValid(playerRef) ||
                    !runner.IsPlayerCommitted(playerRef))
                    return false;

                playerId = playerRef.PlayerId;
                if (!_confirmedPeers.Contains(playerId)) return false;

                NetworkObject playerObject = runner.GetPlayerObject(playerRef);
                if (playerObject == null || !playerObject || !playerObject.IsValid ||
                    playerObject.Id.Raw != networkObjectId || !playerObject.HasStateAuthority ||
                    playerObject.Runner != runner || playerObject.InputAuthority != playerRef)
                    return false;

                if (!TryResolvePlayer(playerId, out playerManager, out target) ||
                    playerManager.Object == null || !playerManager.Object ||
                    playerManager.Object != playerObject || !playerManager.HasStateAuthority ||
                    playerManager.Runner != runner || playerManager.dead)
                    return false;

                return true;
            }
            catch
            {
                playerId = -1;
                playerManager = null;
                target = null;
                return false;
            }
        }

        public bool AllActivePlayersConfirmed()
        {
            if (!IsHost || IsSolo) return false;
            int local = LocalPlayerId;
            int[] active = ActivePlayerIds();
            if (active.Length < 2) return false;
            for (int index = 0; index < active.Length; index++)
            {
                int playerId = active[index];
                if (playerId == local) continue;
                if (!_confirmedPeers.Contains(playerId)) return false;
            }
            return _confirmedPeers.Count == active.Length - 1;
        }

        /// <summary>Client-to-host discovery. The host is the only unconfirmed endpoint used.</summary>
        public bool SendToHost(ArraySegment<byte> frame)
        {
            if (_disposed || IsHost || IsSolo || !CanSend(isHost: false) || !ValidFrame(frame))
                return false;

            try
            {
                NetworkRunner runner = GetRunner();
                if (runner == null) return false;
                runner.SendReliableDataToServer(Key(), Payload(frame));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("Fusion send-to-host failed: " + ex.Message);
                return false;
            }
        }

        public void ConfirmPeer(int playerId)
        {
            if (!IsHost || playerId < 0 || playerId == LocalPlayerId || !IsActivePlayer(playerId))
                return;
            _confirmedPeers.Add(playerId);
        }

        public void ForgetPeer(int playerId) => _confirmedPeers.Remove(playerId);

        public int[] ConfirmedPeerIds()
        {
            var result = new int[_confirmedPeers.Count];
            _confirmedPeers.CopyTo(result);
            return result;
        }

        public bool SendToConfirmedPeer(int playerId, ArraySegment<byte> frame)
        {
            if (_disposed || !IsHost || !_confirmedPeers.Contains(playerId) ||
                !CanSend(isHost: true) || !ValidFrame(frame))
                return false;

            try
            {
                NetworkRunner runner = GetRunner();
                if (runner == null || !TryGetPlayerRef(playerId, out PlayerRef player)) return false;
                runner.SendReliableDataToPlayer(player, Key(), Payload(frame));
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"Fusion send to peer {playerId} failed: {ex.Message}");
                ForgetPeer(playerId);
                return false;
            }
        }

        public void BroadcastConfirmed(ArraySegment<byte> frame)
        {
            int[] peers = ConfirmedPeerIds();
            for (int index = 0; index < peers.Length; index++)
                SendToConfirmedPeer(peers[index], frame);
        }

        internal void Dispatch(int senderPlayerId, ArraySegment<byte> payload)
        {
            if (_disposed || senderPlayerId < 0 || !IsActivePlayer(senderPlayerId) ||
                !ValidFrame(payload))
                return;

            try { FrameReceived?.Invoke(senderPlayerId, payload); }
            catch (Exception ex) { Log.Error("network frame handler failed: " + ex.Message); }
        }

        public void ResetPeers()
        {
            _confirmedPeers.Clear();
            _warnedTransferMode = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            FrameReceived = null;
            ResetPeers();
            ReceiveHook.Remove(this);
        }

        private bool CanSend(bool isHost)
        {
            if (!Available) return false;
            if (ReliableDataPermitted(isHost)) return true;

            if (!_warnedTransferMode)
            {
                _warnedTransferMode = true;
                Log.Warn("Fusion reliable-data mode does not permit this direction; multiplayer NPC disabled");
            }
            return false;
        }

        private static bool ReliableDataPermitted(bool isHost)
        {
            try
            {
                Type configType = typeof(NetworkProjectConfig);
                object global = ReadStaticMember(configType, "Global");
                object network = ReadMember(global, "Network");
                int modes = Convert.ToInt32(ReadMember(network, "ReliableDataTransferModes"));
                int required = isHost ? 2 : 1;
                return (modes & required) == required;
            }
            catch { return false; }
        }

        private static NetworkRunner GetRunner()
        {
            try
            {
                FusionNetworkManager network = FusionNetworkManager.Instance;
                if (network == null || !network || !network.isActiveAndEnabled) return null;
                NetworkRunner runner = network.GetRunner();
                if (runner == null || !runner || !runner.IsRunning) return null;
                return runner;
            }
            catch { return null; }
        }

        private bool TryGetPlayerRef(int playerId, out PlayerRef result)
        {
            NetworkRunner runner = GetRunner();
            if (runner != null)
            {
                foreach (object value in Enumerate(runner.ActivePlayers))
                {
                    if (!TryPlayerRef(value, out PlayerRef candidate) ||
                        candidate.PlayerId != playerId)
                        continue;
                    result = candidate;
                    return true;
                }
            }

            result = default;
            return false;
        }

        private static bool TryPlayerRef(object value, out PlayerRef result)
        {
            if (value is PlayerRef typed)
            {
                result = typed;
                return true;
            }
            result = default;
            return false;
        }

        private static ReliableKey Key() => ReliableKey.FromInts(
            EncounterProtocol.ReliableKey.Part0,
            EncounterProtocol.ReliableKey.Part1,
            EncounterProtocol.ReliableKey.Part2,
            EncounterProtocol.ReliableKey.Part3);

        private static Il2CppStructArray<byte> Payload(ArraySegment<byte> frame)
        {
            var bytes = new byte[frame.Count];
            if (frame.Count > 0)
                Buffer.BlockCopy(frame.Array, frame.Offset, bytes, 0, frame.Count);
            return new Il2CppStructArray<byte>(bytes);
        }

        private static bool ValidFrame(ArraySegment<byte> frame) =>
            frame.Array != null && frame.Offset >= 0 && frame.Count >= 4 &&
            frame.Count <= MaximumFrameBytes && frame.Offset <= frame.Array.Length - frame.Count;

        private static IEnumerable<object> Enumerate(object collection)
        {
            if (collection == null) yield break;
            if (collection is IEnumerable managed)
            {
                foreach (object item in managed) yield return item;
                yield break;
            }

            object enumerator;
            try
            {
                MethodInfo getEnumerator = collection.GetType().GetMethod(
                    "GetEnumerator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                enumerator = getEnumerator?.Invoke(collection, null);
            }
            catch { yield break; }

            if (enumerator == null) yield break;
            Type enumeratorType = enumerator.GetType();
            MethodInfo moveNext = enumeratorType.GetMethod(
                "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            object moveNextTarget = enumerator;
            if (moveNext == null)
            {
                moveNextTarget = ProjectIl2CppInterface(
                    enumerator, "Il2CppSystem.Collections.IEnumerator");
                moveNext = moveNextTarget?.GetType().GetMethod(
                    "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
            }
            PropertyInfo current = enumeratorType.GetProperty(
                "Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext == null || current == null) yield break;

            while (true)
            {
                bool hasNext;
                try { hasNext = Convert.ToBoolean(moveNext.Invoke(moveNextTarget, null)); }
                catch { yield break; }
                if (!hasNext) yield break;

                object item;
                try { item = current.GetValue(enumerator); }
                catch { yield break; }
                yield return item;
            }
        }

        private static object ProjectIl2CppInterface(object instance, string fullName)
        {
            try
            {
                Type projectedType = FindType(fullName);
                object pointerValue = ReadMember(instance, "Pointer");
                if (projectedType == null || pointerValue is not IntPtr pointer || pointer == IntPtr.Zero)
                    return null;
                ConstructorInfo constructor = projectedType.GetConstructor(new[] { typeof(IntPtr) });
                return constructor?.Invoke(new object[] { pointer });
            }
            catch { return null; }
        }

        private static object ReadStaticMember(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            return type?.GetProperty(name, flags)?.GetValue(null) ?? type?.GetField(name, flags)?.GetValue(null);
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = instance.GetType();
            return type.GetProperty(name, flags)?.GetValue(instance) ??
                   type.GetField(name, flags)?.GetValue(instance);
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }
    }
}

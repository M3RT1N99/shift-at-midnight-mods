using System;
using System.Collections.Generic;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// Carries our frames over the game's existing Photon Fusion connection.
    ///
    /// Verified against this build's metadata:
    ///   Il2CppFusion.NetworkRunner.SendReliableDataToServer(
    ///       Il2CppFusion.Sockets.ReliableKey,
    ///       Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray&lt;byte&gt;)
    ///   Il2CppFusion.NetworkRunner.SendReliableDataToPlayer(
    ///       Il2CppFusion.PlayerRef, Il2CppFusion.Sockets.ReliableKey,
    ///       Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray&lt;byte&gt;)
    ///   Il2CppFusion.INetworkRunnerCallbacks.OnReliableDataReceived(
    ///       NetworkRunner, PlayerRef, ReliableKey, Il2CppSystem.ArraySegment&lt;byte&gt;)
    ///   NetworkRunner.Instances (static), Tick, TickRate, LocalPlayer, ActivePlayers,
    ///   IsServer, IsRunning, GameMode
    ///   Il2CppFusion.Sockets.ReliableKey.FromInts(k0,k1,k2,k3)
    ///
    /// The receive payload is an Il2CppSystem value-type wrapper, not a managed
    /// System.ArraySegment&lt;byte&gt;. A future receive hook must copy Array/Offset/Count into
    /// managed memory before calling Dispatch and before returning to Fusion.
    /// Fusion's reliable layer slices and sequences arbitrary payloads itself, so we do not
    /// fragment anything by hand.
    ///
    /// IMPORTANT - the send gate. The binary contains
    ///   "Disconnecting client for sending reliable data when not allowed"
    /// so an unpermitted send does not throw, it drops the player out of the session. The
    /// shipped NetworkProjectConfig has ReliableDataTransferModes = 3 (both directions
    /// allowed), but a patch could change that, and the cost of being wrong is other
    /// people's sessions. Every send is therefore gated on a runtime check, and if the
    /// check fails we go quiet rather than risk it.
    ///
    /// Receiving: implementing INetworkRunnerCallbacks from a plugin would mean injecting a
    /// managed type that implements an Il2Cpp interface, which is the fragile option. The
    /// game already has its own callback implementation (FusionCallbackBase /
    /// FusionNetworkManager), so the robust path is to patch that and forward to us. This
    /// class does not care which mechanism is used - whoever receives calls Dispatch().
    /// </summary>
    internal sealed class SyncTransport
    {
        private readonly Config _cfg;
        private readonly List<Action<int, SyncProtocol.Reader>> _handlers = new();
        private readonly HashSet<int> _confirmedPeers = new();

        private bool _sendBlocked;
        private bool _warnedBlocked;

        public SyncTransport(Config cfg) { _cfg = cfg; }

        /// <summary>True when we are in a live session with at least the runner available.</summary>
        public bool Available => RunnerBridge.IsRunning && RunnerBridge.ReceiveReady;

        public bool IsHost => RunnerBridge.IsServer;

        public int CurrentTick => RunnerBridge.Tick;

        public float TickRate => RunnerBridge.TickRate;

        /// <summary>Registers a frame handler. Receives (senderPlayerId, reader).</summary>
        public void OnFrame(Action<int, SyncProtocol.Reader> handler)
        {
            if (handler != null) _handlers.Add(handler);
        }

        /// <summary>
        /// Marks a player as running a compatible MidnightRadio version. Broadcasts are
        /// deliberately limited to this set: sending our reliable-data key to arbitrary
        /// vanilla clients is both wasteful and an unnecessary compatibility risk.
        /// </summary>
        public void ConfirmPeer(int playerId)
        {
            if (playerId >= 0 && playerId != RunnerBridge.LocalPlayerId)
                _confirmedPeers.Add(playerId);
        }

        public void ForgetPeer(int playerId) => _confirmedPeers.Remove(playerId);

        public int[] ConfirmedPeerIds()
        {
            var result = new int[_confirmedPeers.Count];
            _confirmedPeers.CopyTo(result);
            return result;
        }

        /// <summary>
        /// Entry point for received bytes, called by whatever receive mechanism is wired up.
        /// Anything malformed is dropped silently - a peer on a different mod version must
        /// never be able to break us.
        /// </summary>
        public void Dispatch(int senderPlayerId, ArraySegment<byte> payload)
        {
            Log.Guard("SyncTransport.Dispatch", () =>
            {
                var reader = new SyncProtocol.Reader(payload);
                if (!reader.Ok) return;

                if (reader.PeerVersion != SyncProtocol.Version)
                {
                    Log.Debug($"ignoring frame from player {senderPlayerId}: protocol v{reader.PeerVersion}");
                    return;
                }

                if (_cfg.Sync.BlockedPlayers.Contains(senderPlayerId.ToString())) return;

                // Readers are cursors. Give every handler a fresh one so one consumer
                // cannot accidentally leave the next consumer at the end of the frame.
                foreach (var h in _handlers)
                {
                    var handlerReader = new SyncProtocol.Reader(payload);
                    Log.Guard("frame handler", () => h(senderPlayerId, handlerReader));
                }

                // Relaying is intentionally a SyncSession decision, after permissions and
                // state revision have been validated. Blindly relaying a well-formed frame
                // would let any peer bypass AnyoneCanQueue/AnyoneCanSkip.
            });
        }

        /// <summary>Client -> host. The only send a non-host makes.</summary>
        public void SendToHost(ArraySegment<byte> payload)
        {
            if (!CanSend()) return;
            Log.Guard("SendToHost", () => RunnerBridge.SendToServer(SyncProtocol.KeyParts, payload));
        }

        /// <summary>
        /// True once a receive path is wired up. Without it we can send but never hear an
        /// answer, which would look like sync while silently being one-way - so the session
        /// treats "not ready" as "stay local".
        /// </summary>
        public bool ReceiveReady => RunnerBridge.ReceiveReady;

        /// <summary>Host -> one specific peer. Used for late-joiner snapshots.</summary>
        public void SendToPlayer(int playerId, ArraySegment<byte> payload)
        {
            if (!CanSend() || !IsHost) return;
            if (playerId == RunnerBridge.LocalPlayerId) return;
            Log.Guard("SendToPlayer", () => RunnerBridge.SendToPlayer(playerId, SyncProtocol.KeyParts, payload));
        }

        /// <summary>Host -> every other modded peer.</summary>
        public void Broadcast(ArraySegment<byte> payload, int exceptPlayerId = -1)
        {
            if (!CanSend() || !IsHost) return;

            Log.Guard("Broadcast", () =>
            {
                foreach (int pid in ConfirmedPeerIds())
                {
                    if (pid == exceptPlayerId) continue;
                    if (pid == RunnerBridge.LocalPlayerId) continue;
                    RunnerBridge.SendToPlayer(pid, SyncProtocol.KeyParts, payload);
                }
            });
        }

        /// <summary>
        /// Gate every send on the session actually permitting reliable data. Failing closed
        /// costs us the feature; failing open costs the player their connection.
        /// </summary>
        private bool CanSend()
        {
            if (!_cfg.Sync.Enabled) return false;
            if (!Available) return false;

            if (_sendBlocked) return false;

            if (!RunnerBridge.ReliableDataPermitted(IsHost))
            {
                _sendBlocked = true;
                if (!_warnedBlocked)
                {
                    _warnedBlocked = true;
                    Log.Warn("reliable data is not permitted in this session - synced music disabled, " +
                             "local playback still works");
                }
                return false;
            }

            return true;
        }

        /// <summary>Called on session teardown so a new session re-evaluates the gate.</summary>
        public void Reset()
        {
            _sendBlocked = false;
            _warnedBlocked = false;
            _confirmedPeers.Clear();
        }
    }
}

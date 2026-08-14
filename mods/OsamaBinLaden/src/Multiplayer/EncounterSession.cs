using System;
using System.Collections.Generic;
using Il2Cpp;
using UnityEngine;

namespace OsamaBinLaden.Multiplayer
{
    /// <summary>
    /// Runs the Hunt encounter over Fusion once a live, non-solo session is positively
    /// confirmed. The host is the only machine that ever calls
    /// <c>PlayerManager.TakeDamage</c>; every other peer only ever renders a cosmetic mirror
    /// of the host's character built from the Spawn/Detonate/Cancel frames it receives. A
    /// player who never completes the handshake (does not run this mod, or has it disabled)
    /// is never selected as a target and never receives a frame, so it never sees or is
    /// affected by the encounter - unmodded players are unaffected by design, not by luck.
    ///
    /// Two independent state machines share this instance because the local player's host/
    /// client role can only be known at runtime: <see cref="UpdateHost"/> simulates and
    /// broadcasts; <see cref="UpdateClient"/> only ever renders what the host reports. A role
    /// change (extremely rare - Fusion host migration, if the game ever exposes it) resets
    /// both to a clean slate rather than mixing state from two roles.
    /// </summary>
    internal sealed class EncounterSession : IDisposable
    {
        private enum ClientHandshakePhase
        {
            NotStarted,
            AwaitingAck,
            Ready
        }

        private sealed class PendingHandshake
        {
            public ulong ClientNonce;
            public ulong HostNonce;
            public float IssuedAtRealtime;
        }

        private sealed class ValidatedPeer
        {
            public ulong ClientNonce;
            public ulong HostNonce;
        }

        private sealed class HostEncounter
        {
            public ulong Id;
            public int TargetPlayerId;
            public int TargetRawEncoded;
            public uint TargetNetworkId;
            public RuntimeCharacter Character;
        }

        private readonly Config _config;
        private readonly FusionTransport _transport = new FusionTransport();
        private readonly System.Random _random = new System.Random();

        private bool? _wasHost;

        // Host-role state.
        private readonly Dictionary<int, PendingHandshake> _pendingHandshakes = new Dictionary<int, PendingHandshake>();
        private readonly Dictionary<int, ValidatedPeer> _validatedPeers = new Dictionary<int, ValidatedPeer>();
        private ulong _hostEpoch;
        private ulong _hostSequence;
        private float _nextMarkerPublishRealtime;
        private float _nextHeartbeatRealtime;
        private bool _huntObserved;
        private bool _spawnAttemptedThisHunt;
        private HostEncounter _hostEncounter;

        // Client-role state.
        private ClientHandshakePhase _clientPhase = ClientHandshakePhase.NotStarted;
        private ulong _clientNonce;
        private ulong _clientSequence;
        private ulong _learnedHostEpoch;
        private ulong _learnedHostNonce;
        private int _learnedHostPlayerId = -1;
        private float _clientHandshakeStartedRealtime;
        private float _clientLastHostMessageRealtime;
        private readonly SequenceGuard _clientReplayGuard = new SequenceGuard();
        private ulong _clientEncounterId;
        private int _clientTargetPlayerId = -1;
        private RuntimeCharacter _clientCharacter;

        private bool _disposed;

        public EncounterSession(Config config)
        {
            _config = config;
            _transport.FrameReceived += OnFrameReceived;
        }

        /// <summary>True once a real, non-solo Fusion runner is live. Ambiguous or missing
        /// runner state - menus, loading, an uninitialised network manager - counts as false,
        /// the same fail-closed rule <see cref="SessionGate"/> applies to solo play.</summary>
        public bool IsActive => _transport.RunnerLive && !_transport.IsSolo;

        public void Update(float deltaTime)
        {
            if (_disposed || !IsActive) return;

            float realtime = Time.realtimeSinceStartup;
            bool isHost = _transport.IsHost;
            if (_wasHost != isHost)
            {
                ResetHostState(sendCancel: false);
                ResetClientState(disposeCharacter: true);
                if (isHost) _hostEpoch = NextRandomNonzeroId();
                _wasHost = isHost;
                Log.Debug(isHost
                    ? "multiplayer: acting as the encounter host"
                    : "multiplayer: acting as an encounter client");
            }

            _transport.Update(realtime);

            if (isHost) UpdateHost(deltaTime, realtime);
            else UpdateClient(deltaTime, realtime);
        }

        /// <summary>Drops every local object and handshake without touching the receive hook,
        /// so the session can resume cleanly the next time <see cref="IsActive"/> is true.</summary>
        public void Reset()
        {
            if (_disposed) return;
            ResetHostState(sendCancel: true);
            ResetClientState(disposeCharacter: true);
            _wasHost = null;
            _transport.ResetPeers();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ResetHostState(sendCancel: false);
            ResetClientState(disposeCharacter: true);
            _transport.Dispose();
        }

        // ----------------------------------------------------------------- host role -----

        private void UpdateHost(float deltaTime, float realtime)
        {
            if (realtime >= _nextMarkerPublishRealtime)
            {
                _transport.PublishHostMarker();
                _nextMarkerPublishRealtime = realtime + _config.Multiplayer.HostMarkerRepublishSeconds;
            }

            PruneHostHandshakes(realtime);

            if (realtime >= _nextHeartbeatRealtime)
            {
                _nextHeartbeatRealtime = realtime + _config.Multiplayer.HeartbeatIntervalSeconds;
                foreach (KeyValuePair<int, ValidatedPeer> peer in _validatedPeers)
                    SendToPeer(peer.Key, peer.Value, EncounterMessageType.Heartbeat, EncounterReason.None, null);
            }

            HuntManager hunt = HuntManager.Instance;
            bool huntActive = hunt != null && hunt && hunt.isActiveAndEnabled && hunt.huntInProgress;

            if (!huntActive)
            {
                if (_huntObserved) DisposeHostEncounter(sendCancel: true, EncounterReason.HuntEnded);
                _huntObserved = false;
                _spawnAttemptedThisHunt = false;
                return;
            }

            if (!_huntObserved)
            {
                _huntObserved = true;
                TryStartHostEncounter(hunt);
            }

            if (_hostEncounter == null) return;

            // Re-prove the target every tick, not only at detonation: a disconnect or a
            // destroyed object must cancel the encounter immediately, the same way an
            // ambiguous solo session drops the local character before Update ever reaches
            // the pursuit logic.
            if (!_transport.TryResolvePlayer(_hostEncounter.TargetPlayerId, out _, out Transform currentTarget))
            {
                DisposeHostEncounter(sendCancel: true, EncounterReason.InvalidState);
                return;
            }

            _hostEncounter.Character.SetTarget(currentTarget);
            _hostEncounter.Character.Tick(deltaTime);
            if (_hostEncounter.Character.IsFinished) DisposeHostEncounter(sendCancel: false);
        }

        private void TryStartHostEncounter(HuntManager hunt)
        {
            if (_spawnAttemptedThisHunt) return;
            _spawnAttemptedThisHunt = true;

            if (_random.NextDouble() > _config.Spawn.ChancePerEligibleEncounter)
            {
                Log.Debug("multiplayer Hunt encounter skipped by configured chance");
                return;
            }

            if (!TryPickEligibleTarget(out int targetPlayerId, out int targetRawEncoded,
                    out uint targetNetworkId, out Transform targetTransform))
            {
                Log.Debug("multiplayer Hunt encounter skipped: no eligible target has the mod");
                return;
            }

            var points = hunt.huntSpawnPoints;
            int spawnPointCount = points != null ? points.Length : 0;
            Vector3 spawnPosition = SpawnPlacement.Resolve(
                spawnPointCount,
                index => points[index],
                targetTransform.position,
                targetTransform.forward,
                _config.Spawn.MinimumSpawnDistanceMeters,
                _config.Spawn.MaximumSpawnDistanceMeters);

            var options = new RuntimeCharacterOptions
            {
                RunSpeed = _config.Attack.RunSpeedMetersPerSecond,
                TriggerDistance = _config.Attack.DetonationDistanceMeters,
                FuseSeconds = _config.Attack.FuseSeconds,
                MaximumLifetimeSeconds = _config.Spawn.MaximumLifetimeSeconds,
                VisualScale = _config.Effects.VisualScale,
                ScreamEnabled = _config.Effects.ScreamEnabled,
                ScreamVolume = _config.Effects.ScreamVolume,
                ExplosionVisualRadius = _config.Effects.ExplosionRadiusMeters
            };

            RuntimeCharacter character;
            try
            {
                character = new RuntimeCharacter(targetTransform, spawnPosition, options, OnHostDetonated);
            }
            catch (Exception ex)
            {
                Log.Warn($"multiplayer Hunt encounter could not spawn a character: {ex.Message}");
                return;
            }

            ulong encounterId = NextRandomNonzeroId();
            _hostEncounter = new HostEncounter
            {
                Id = encounterId,
                TargetPlayerId = targetPlayerId,
                TargetRawEncoded = targetRawEncoded,
                TargetNetworkId = targetNetworkId,
                Character = character
            };

            foreach (KeyValuePair<int, ValidatedPeer> peer in _validatedPeers)
            {
                SendToPeer(peer.Key, peer.Value, EncounterMessageType.Spawn, EncounterReason.HuntStarted, message =>
                {
                    message.EncounterId = encounterId;
                    message.TargetPlayerId = targetPlayerId;
                    message.TargetPlayerRawEncoded = targetRawEncoded;
                    message.TargetNetworkId = targetNetworkId;
                    message.SpawnX = spawnPosition.x;
                    message.SpawnY = spawnPosition.y;
                    message.SpawnZ = spawnPosition.z;
                    message.Config = BuildConfigSnapshot();
                });
            }

            Log.Info($"multiplayer Hunt encounter started against player {targetPlayerId}");
        }

        /// <summary>Eligible targets are the host itself plus every validated peer - players
        /// who proved, by completing the handshake, that they run this mod. Anyone else is
        /// never even considered, so the encounter can never touch an unmodded player.</summary>
        private bool TryPickEligibleTarget(
            out int playerId, out int rawEncoded, out uint networkObjectId, out Transform target)
        {
            playerId = -1;
            rawEncoded = 0;
            networkObjectId = 0;
            target = null;

            var candidates = new List<int>(_validatedPeers.Count + 1) { _transport.LocalPlayerId };
            candidates.AddRange(_validatedPeers.Keys);

            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            foreach (int candidateId in candidates)
            {
                if (!_transport.TryGetPlayerIdentity(candidateId, out int candidateRaw,
                        out uint candidateNetworkId, out _, out Transform candidateTarget))
                    continue;

                playerId = candidateId;
                rawEncoded = candidateRaw;
                networkObjectId = candidateNetworkId;
                target = candidateTarget;
                return true;
            }

            return false;
        }

        private void OnHostDetonated(RuntimeCharacter.DetonationInfo info)
        {
            HostEncounter encounter = _hostEncounter;
            if (encounter == null) return;

            // Re-prove target identity at the irreversible gameplay boundary, exactly like the
            // solo controller re-proves solo mode before calling TakeDamage. A host-on-self
            // encounter needs the same local resolution solo mode already trusts, not a claim
            // about a remote player: FusionTransport deliberately never treats the host's own
            // id as a "confirmed peer", so RevalidateHostDamageTarget can never succeed for it.
            bool revalidated;
            PlayerManager playerManager;
            Transform currentTarget;
            if (encounter.TargetPlayerId == _transport.LocalPlayerId)
            {
                revalidated = _transport.TryResolvePlayer(
                    encounter.TargetPlayerId, out playerManager, out currentTarget);
            }
            else
            {
                revalidated = _transport.RevalidateHostDamageTarget(
                    encounter.TargetRawEncoded, encounter.TargetNetworkId,
                    out _, out playerManager, out currentTarget);
            }

            if (revalidated)
            {
                float distance = Vector3.Distance(info.Position, currentTarget.position);
                float damage = ExplosionMath.CalculateDamage(
                    distance,
                    _config.Attack.DetonationDistanceMeters,
                    _config.Effects.ExplosionRadiusMeters,
                    _config.Effects.ExplosionDamage);

                if (damage > 0f && !playerManager.dead)
                {
                    playerManager.TakeDamage(damage, true, "Explosion");
                    Log.Info($"multiplayer detonation hit player {encounter.TargetPlayerId} ({damage:0.#} damage)");
                }
            }
            else
            {
                Log.Warn("multiplayer detonation stayed cosmetic because the target could not be re-confirmed");
            }

            EncounterReason reason = ToReason(info.Cause);
            foreach (KeyValuePair<int, ValidatedPeer> peer in _validatedPeers)
            {
                SendToPeer(peer.Key, peer.Value, EncounterMessageType.Detonate, reason, message =>
                {
                    message.EncounterId = encounter.Id;
                    message.TargetPlayerId = encounter.TargetPlayerId;
                    message.TargetPlayerRawEncoded = encounter.TargetRawEncoded;
                    message.TargetNetworkId = encounter.TargetNetworkId;
                    message.SpawnX = info.Position.x;
                    message.SpawnY = info.Position.y;
                    message.SpawnZ = info.Position.z;
                    message.Config = BuildConfigSnapshot();
                });
            }
        }

        private void DisposeHostEncounter(bool sendCancel, EncounterReason reason = EncounterReason.HuntEnded)
        {
            HostEncounter encounter = _hostEncounter;
            _hostEncounter = null;
            if (encounter == null) return;

            if (sendCancel)
            {
                foreach (KeyValuePair<int, ValidatedPeer> peer in _validatedPeers)
                {
                    SendToPeer(peer.Key, peer.Value, EncounterMessageType.Cancel, reason, message =>
                    {
                        message.EncounterId = encounter.Id;
                        message.TargetPlayerId = encounter.TargetPlayerId;
                        message.TargetPlayerRawEncoded = encounter.TargetRawEncoded;
                        message.TargetNetworkId = encounter.TargetNetworkId;
                        message.Config = BuildConfigSnapshot();
                    });
                }
            }

            encounter.Character.Dispose();
        }

        private void HandleHostReceive(int senderPlayerId, EncounterMessage message)
        {
            if (EncounterProtocol.RequiresHostSender(message.Type)) return; // a peer can never send these
            if (!_transport.IsActivePlayer(senderPlayerId)) return;

            switch (message.Type)
            {
                case EncounterMessageType.Hello:
                    HandleHello(senderPlayerId, message);
                    break;
                case EncounterMessageType.Ready:
                    HandleReady(senderPlayerId, message);
                    break;
            }
        }

        private void HandleHello(int senderPlayerId, EncounterMessage message)
        {
            if (_validatedPeers.ContainsKey(senderPlayerId)) return; // already trusted; ignore a stray re-hello

            var pending = new PendingHandshake
            {
                ClientNonce = message.ClientNonce,
                HostNonce = NextRandomNonzeroId(),
                IssuedAtRealtime = Time.realtimeSinceStartup
            };
            _pendingHandshakes[senderPlayerId] = pending;

            // Confirming the peer here only grants "may receive protocol bytes" at the
            // transport layer; it is not trusted for target selection until Ready validates.
            _transport.ConfirmPeer(senderPlayerId);

            EncounterMessage ack = new EncounterMessage
            {
                Type = EncounterMessageType.HelloAck,
                Reason = EncounterReason.None,
                Sequence = NextHostSequence(),
                HostEpoch = _hostEpoch,
                ClientNonce = pending.ClientNonce,
                HostNonce = pending.HostNonce,
                HostPlayerId = _transport.LocalPlayerId,
                HostTick = (ulong)Math.Max(0, _transport.CurrentTick)
            };
            Encode(ack, out byte[] packet);
            if (packet != null) _transport.SendToConfirmedPeer(senderPlayerId, new ArraySegment<byte>(packet));
        }

        private void HandleReady(int senderPlayerId, EncounterMessage message)
        {
            if (!_pendingHandshakes.TryGetValue(senderPlayerId, out PendingHandshake pending)) return;

            if (message.HostEpoch != _hostEpoch || message.ClientNonce != pending.ClientNonce ||
                message.HostNonce != pending.HostNonce || message.HostPlayerId != _transport.LocalPlayerId)
            {
                // A stale or mismatched Ready never gets a second chance; the peer must say
                // Hello again and go through a fresh challenge.
                _pendingHandshakes.Remove(senderPlayerId);
                _transport.ForgetPeer(senderPlayerId);
                return;
            }

            _pendingHandshakes.Remove(senderPlayerId);
            _validatedPeers[senderPlayerId] = new ValidatedPeer
            {
                ClientNonce = pending.ClientNonce,
                HostNonce = pending.HostNonce
            };
            Log.Debug($"multiplayer peer {senderPlayerId} completed the encounter handshake");
        }

        private void PruneHostHandshakes(float realtime)
        {
            List<int> expiredHandshakes = null;
            foreach (KeyValuePair<int, PendingHandshake> pair in _pendingHandshakes)
            {
                if (realtime - pair.Value.IssuedAtRealtime <= _config.Multiplayer.HandshakeTimeoutSeconds) continue;
                (expiredHandshakes ??= new List<int>()).Add(pair.Key);
            }
            if (expiredHandshakes != null)
            {
                foreach (int id in expiredHandshakes)
                {
                    _pendingHandshakes.Remove(id);
                    _transport.ForgetPeer(id);
                }
            }

            if (_validatedPeers.Count == 0) return;

            // One active-player snapshot for every validated peer, instead of one reflection
            // walk over Fusion's player list per peer.
            var activePlayers = new HashSet<int>(_transport.ActivePlayerIds());
            List<int> departedPeers = null;
            foreach (int peerId in _validatedPeers.Keys)
            {
                if (activePlayers.Contains(peerId)) continue;
                (departedPeers ??= new List<int>()).Add(peerId);
            }
            if (departedPeers != null)
            {
                foreach (int id in departedPeers)
                {
                    _validatedPeers.Remove(id);
                    _transport.ForgetPeer(id);
                    if (_hostEncounter != null && _hostEncounter.TargetPlayerId == id)
                        DisposeHostEncounter(sendCancel: true, EncounterReason.InvalidState);
                }
            }
        }

        private void SendToPeer(
            int peerId,
            ValidatedPeer peer,
            EncounterMessageType type,
            EncounterReason reason,
            Action<EncounterMessage> customize)
        {
            EncounterMessage message = new EncounterMessage
            {
                Type = type,
                Reason = reason,
                Sequence = NextHostSequence(),
                HostEpoch = _hostEpoch,
                ClientNonce = peer.ClientNonce,
                HostNonce = peer.HostNonce,
                HostPlayerId = _transport.LocalPlayerId,
                HostTick = (ulong)Math.Max(0, _transport.CurrentTick)
            };
            customize?.Invoke(message);

            Encode(message, out byte[] packet);
            if (packet != null) _transport.SendToConfirmedPeer(peerId, new ArraySegment<byte>(packet));
        }

        private void ResetHostState(bool sendCancel)
        {
            DisposeHostEncounter(sendCancel);
            _pendingHandshakes.Clear();
            _validatedPeers.Clear();
            _huntObserved = false;
            _spawnAttemptedThisHunt = false;
        }

        // --------------------------------------------------------------- client role -----

        private void UpdateClient(float deltaTime, float realtime)
        {
            if (_clientPhase == ClientHandshakePhase.Ready && _learnedHostPlayerId >= 0 &&
                !_transport.IsActivePlayer(_learnedHostPlayerId))
            {
                Log.Debug("multiplayer host disconnected; resetting the encounter handshake");
                ResetClientState(disposeCharacter: true);
            }

            switch (_clientPhase)
            {
                case ClientHandshakePhase.NotStarted:
                    if (_transport.HostMarkerPresent()) SendHello(realtime);
                    break;

                case ClientHandshakePhase.AwaitingAck:
                    if (realtime - _clientHandshakeStartedRealtime > _config.Multiplayer.HandshakeTimeoutSeconds)
                        _clientPhase = ClientHandshakePhase.NotStarted; // retried next tick
                    break;

                case ClientHandshakePhase.Ready:
                    if (realtime - _clientLastHostMessageRealtime > _config.Multiplayer.PeerTimeoutSeconds)
                    {
                        Log.Debug("multiplayer host went quiet; resetting the encounter handshake");
                        ResetClientState(disposeCharacter: true);
                    }
                    break;
            }

            if (_clientCharacter == null) return;

            if (_clientPhase != ClientHandshakePhase.Ready ||
                !_transport.TryResolvePlayer(_clientTargetPlayerId, out _, out Transform mirrorTarget))
            {
                DisposeClientCharacter();
                return;
            }

            _clientCharacter.SetTarget(mirrorTarget);
            _clientCharacter.Tick(deltaTime);
            if (_clientCharacter.IsFinished) DisposeClientCharacter();
        }

        private void SendHello(float realtime)
        {
            _clientNonce = NextRandomNonzeroId();
            EncounterMessage hello = new EncounterMessage
            {
                Type = EncounterMessageType.Hello,
                Reason = EncounterReason.None,
                Sequence = NextClientSequence(),
                ClientNonce = _clientNonce,
                HostPlayerId = -1
            };

            Encode(hello, out byte[] packet);
            if (packet != null && _transport.SendToHost(new ArraySegment<byte>(packet)))
            {
                _clientPhase = ClientHandshakePhase.AwaitingAck;
                _clientHandshakeStartedRealtime = realtime;
            }
        }

        private void HandleClientReceive(int senderPlayerId, EncounterMessage message)
        {
            if (!EncounterProtocol.RequiresHostSender(message.Type)) return; // only the host may send these

            switch (message.Type)
            {
                case EncounterMessageType.HelloAck:
                    HandleHelloAck(senderPlayerId, message);
                    break;
                case EncounterMessageType.Heartbeat:
                    HandleHostSteadyMessage(senderPlayerId, message, null);
                    break;
                case EncounterMessageType.Spawn:
                    HandleHostSteadyMessage(senderPlayerId, message, HandleSpawn);
                    break;
                case EncounterMessageType.Detonate:
                    HandleHostSteadyMessage(senderPlayerId, message, HandleDetonate);
                    break;
                case EncounterMessageType.Cancel:
                    HandleHostSteadyMessage(senderPlayerId, message, HandleCancel);
                    break;
            }
        }

        private void HandleHelloAck(int senderPlayerId, EncounterMessage message)
        {
            if (_clientPhase != ClientHandshakePhase.AwaitingAck) return;
            if (message.ClientNonce != _clientNonce || message.HostPlayerId < 0) return;
            // This is the message that bootstraps trust in "who is the host" for every later
            // steady-state check; the payload's claimed identity must match Fusion's own
            // sender, not be taken on its word.
            if (senderPlayerId != message.HostPlayerId) return;

            _learnedHostEpoch = message.HostEpoch;
            _learnedHostNonce = message.HostNonce;
            _learnedHostPlayerId = senderPlayerId;
            _clientReplayGuard.Reset();
            _clientLastHostMessageRealtime = Time.realtimeSinceStartup;

            EncounterMessage ready = new EncounterMessage
            {
                Type = EncounterMessageType.Ready,
                Reason = EncounterReason.None,
                Sequence = NextClientSequence(),
                HostEpoch = _learnedHostEpoch,
                ClientNonce = _clientNonce,
                HostNonce = _learnedHostNonce,
                HostPlayerId = _learnedHostPlayerId
            };

            Encode(ready, out byte[] packet);
            if (packet != null && _transport.SendToHost(new ArraySegment<byte>(packet)))
                _clientPhase = ClientHandshakePhase.Ready;
        }

        /// <summary>
        /// Common gate for every post-handshake host message: the Fusion-verified sender must
        /// be the host we handshook with, the payload must echo our exact challenge, and the
        /// (epoch, sequence) pair must be newer than the last one we accepted. None of this is
        /// authentication by itself - Fusion's own delivery guarantees are - but it is cheap
        /// insurance against a stale or duplicated frame, and it is what the payload's nonce
        /// and sequence fields exist for.
        /// </summary>
        private void HandleHostSteadyMessage(int senderPlayerId, EncounterMessage message, Action<EncounterMessage> handler)
        {
            if (_clientPhase != ClientHandshakePhase.Ready) return;
            if (senderPlayerId != _learnedHostPlayerId) return;
            if (message.HostPlayerId != _learnedHostPlayerId ||
                message.ClientNonce != _clientNonce || message.HostNonce != _learnedHostNonce)
                return;
            if (!_clientReplayGuard.TryAccept(message.HostEpoch, message.Sequence)) return;

            _clientLastHostMessageRealtime = Time.realtimeSinceStartup;
            handler?.Invoke(message);
        }

        private void HandleSpawn(EncounterMessage message)
        {
            DisposeClientCharacter(); // a fresh Spawn always replaces whatever we had, even mid-blast

            if (!_transport.TryResolvePlayer(message.TargetPlayerId, out _, out Transform target))
            {
                Log.Debug("multiplayer Spawn ignored: target could not be resolved locally");
                return;
            }

            var options = new RuntimeCharacterOptions
            {
                RunSpeed = message.Config.RunSpeed,
                TriggerDistance = message.Config.TriggerDistance,
                FuseSeconds = message.Config.FuseSeconds,
                MaximumLifetimeSeconds = message.Config.LifetimeSeconds,
                VisualScale = message.Config.VisualScale,
                ScreamEnabled = message.Config.ScreamVolume > 0f,
                ScreamVolume = message.Config.ScreamVolume,
                ExplosionVisualRadius = message.Config.ExplosionRadius
            };
            Vector3 spawnPosition = new Vector3(message.SpawnX, message.SpawnY, message.SpawnZ);

            try
            {
                // The mirror's callback is intentionally a no-op: only the host ever re-proves
                // authority and calls PlayerManager.TakeDamage.
                _clientCharacter = new RuntimeCharacter(target, spawnPosition, options, _ => { });
                _clientEncounterId = message.EncounterId;
                _clientTargetPlayerId = message.TargetPlayerId;
            }
            catch (Exception ex)
            {
                Log.Warn($"multiplayer mirror character unavailable: {ex.Message}");
                _clientCharacter = null;
            }
        }

        private void HandleDetonate(EncounterMessage message)
        {
            if (_clientCharacter == null || message.EncounterId != _clientEncounterId) return;
            _clientCharacter.Detonate();
        }

        private void HandleCancel(EncounterMessage message)
        {
            if (_clientCharacter == null || message.EncounterId != _clientEncounterId) return;
            DisposeClientCharacter();
        }

        private void DisposeClientCharacter()
        {
            _clientCharacter?.Dispose();
            _clientCharacter = null;
            _clientEncounterId = 0;
            _clientTargetPlayerId = -1;
        }

        private void ResetClientState(bool disposeCharacter)
        {
            _clientPhase = ClientHandshakePhase.NotStarted;
            _clientNonce = 0;
            _learnedHostEpoch = 0;
            _learnedHostNonce = 0;
            _learnedHostPlayerId = -1;
            _clientHandshakeStartedRealtime = 0f;
            _clientLastHostMessageRealtime = 0f;
            _clientReplayGuard.Reset();
            if (disposeCharacter) DisposeClientCharacter();
        }

        // ------------------------------------------------------------------- shared -----

        private void OnFrameReceived(int senderPlayerId, ArraySegment<byte> payload)
        {
            if (_disposed) return;
            if (!EncounterProtocol.TryDecode(payload.Array, payload.Offset, payload.Count, out EncounterMessage message))
                return;

            if (_transport.IsHost) HandleHostReceive(senderPlayerId, message);
            else HandleClientReceive(senderPlayerId, message);
        }

        private EncounterConfigSnapshot BuildConfigSnapshot() => new EncounterConfigSnapshot
        {
            RunSpeed = _config.Attack.RunSpeedMetersPerSecond,
            TriggerDistance = _config.Attack.DetonationDistanceMeters,
            FuseSeconds = _config.Attack.FuseSeconds,
            LifetimeSeconds = _config.Spawn.MaximumLifetimeSeconds,
            VisualScale = _config.Effects.VisualScale,
            ScreamVolume = _config.Effects.ScreamEnabled ? _config.Effects.ScreamVolume : 0f,
            ExplosionRadius = _config.Effects.ExplosionRadiusMeters,
            ExplosionDamage = _config.Effects.ExplosionDamage
        };

        private static EncounterReason ToReason(DetonationCause cause) => cause switch
        {
            DetonationCause.ReachedTarget => EncounterReason.ReachedTarget,
            DetonationCause.LifetimeExpired => EncounterReason.LifetimeExpired,
            _ => EncounterReason.FuseExpired
        };

        private static void Encode(EncounterMessage message, out byte[] packet)
        {
            if (!EncounterProtocol.TryEncode(message, out packet))
            {
                packet = null;
                Log.Warn($"multiplayer: refused to encode an invalid {message.Type} message");
            }
        }

        private ulong NextHostSequence() => ++_hostSequence;

        private ulong NextClientSequence() => ++_clientSequence;

        private ulong NextRandomNonzeroId()
        {
            ulong value;
            do
            {
                value = ((ulong)(uint)_random.Next() << 32) | (uint)_random.Next();
            } while (value == 0);
            return value;
        }
    }
}

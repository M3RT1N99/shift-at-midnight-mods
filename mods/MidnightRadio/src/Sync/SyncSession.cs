using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// The shared listening state: what is playing, where in the track everyone should be,
    /// and what is queued next.
    ///
    /// Authority model - OPEN QUEUE. Any player may add a track or start playback; there is
    /// no host-only DJ. The host is only a sequencer: it stamps an incrementing revision on
    /// every accepted change and relays it, so all peers apply changes in one order. That
    /// gives determinism without taking control away from anyone.
    ///
    /// Only identifiers travel. A peer that cannot resolve a track answers Need and stays
    /// silent rather than desyncing or guessing at a substitute.
    ///
    /// Everything here degrades to plain local playback when there is no session, no
    /// receive path, or no other modded peer.
    /// </summary>
    internal sealed class SyncSession : IDisposable
    {
        private readonly Config _cfg;
        private readonly SyncTransport _transport;
        private readonly SyncClock _clock;

        private readonly Func<string, TrackInfo> _resolveTrack;
        private readonly Action<TrackInfo, float> _playLocal;
        private readonly Action _stopLocal;

        private readonly List<QueueEntry> _queue = new();
        private readonly Dictionary<int, PeerState> _peers = new();

        private int _stateRev;
        private string _currentTrackId;
        private string _currentTitle;
        private bool _helloSent;
        private float _nextHello;

        public SyncSession(
            Config cfg,
            SyncTransport transport,
            SyncClock clock,
            Func<string, TrackInfo> resolveTrack,
            Action<TrackInfo, float> playLocal,
            Action stopLocal)
        {
            _cfg = cfg;
            _transport = transport;
            _clock = clock;
            _resolveTrack = resolveTrack;
            _playLocal = playLocal;
            _stopLocal = stopLocal;

            _transport.OnFrame(HandleFrame);
        }

        internal sealed class QueueEntry
        {
            public string TrackId;
            public string Title;
            public int AddedByPlayerId;
        }

        internal sealed class PeerState
        {
            public string Name = "?";
            public bool CanFetchUrls;
            public string MissingTrackId;
            public SyncProtocol.NeedReason MissingReason;
            public float LastSeen;
        }

        /// <summary>True when synced playback is actually in effect right now.</summary>
        public bool Active => _cfg.Sync.Enabled && _transport.Available && _transport.ReceiveReady;

        public IReadOnlyList<QueueEntry> Queue => _queue;
        public IReadOnlyDictionary<int, PeerState> Peers => _peers;
        public string CurrentTitle => _currentTitle;
        public float DriftMs => _clock.LastDriftMs;

        // ------------------------------------------------------------------ local intent

        /// <summary>
        /// The local player picked a track. Host applies and announces it; a client asks the
        /// host, which keeps ordering consistent when two people press play together.
        /// </summary>
        public void RequestPlay(TrackInfo track)
        {
            if (track == null) return;

            if (!Active)
            {
                _playLocal(track, 0f);           // solo / no session - just play it
                return;
            }

            if (_transport.IsHost) AuthorisePlay(track.Id, track.Title, 0f);
            else _transport.SendToHost(SyncProtocol.QueueAdd(track.Id, track.Title, playNow: true));
        }

        public void RequestQueue(TrackInfo track)
        {
            if (track == null) return;

            if (!Active)
            {
                _queue.Add(new QueueEntry { TrackId = track.Id, Title = track.Title, AddedByPlayerId = -1 });
                return;
            }

            if (_transport.IsHost) AcceptQueueAdd(RunnerBridge.LocalPlayerId, track.Id, track.Title, false);
            else _transport.SendToHost(SyncProtocol.QueueAdd(track.Id, track.Title, playNow: false));
        }

        public void RequestSkip()
        {
            if (!Active) return;
            if (!_cfg.Sync.AnyoneCanSkip && !_transport.IsHost) return;

            if (_transport.IsHost) PlayNextFromQueue();
            else _transport.SendToHost(SyncProtocol.Control(
                SyncProtocol.ControlOp.SkipNext, _transport.CurrentTick, 0f, _stateRev));
        }

        public void RequestPause(bool paused, float position)
        {
            if (!Active) return;

            var op = paused ? SyncProtocol.ControlOp.Pause : SyncProtocol.ControlOp.Resume;
            if (_transport.IsHost) AuthoriseControl(op, position);
            else _transport.SendToHost(SyncProtocol.Control(op, _transport.CurrentTick, position, _stateRev));
        }

        // ------------------------------------------------------------------ host decisions

        private void AuthorisePlay(string trackId, string title, float offset)
        {
            _stateRev++;
            _currentTrackId = trackId;
            _currentTitle = title;

            // Start in the future so every peer has time to load. "Together" then means the
            // same tick rather than the same message arrival.
            int leadTicks = Mathf.Max(1, Mathf.RoundToInt(
                _transport.TickRate * (_cfg.Sync.StartLeadMs / 1000f)));
            int startTick = _transport.CurrentTick + leadTicks;

            var frame = SyncProtocol.NowPlaying(
                SyncProtocol.TrackSource.LocalFile, trackId, title, startTick, offset, 0f, _stateRev);

            _transport.Broadcast(frame);
            ApplyNowPlaying(trackId, title, startTick, offset);
        }

        private void AuthoriseControl(SyncProtocol.ControlOp op, float position)
        {
            _stateRev++;
            _transport.Broadcast(SyncProtocol.Control(op, _transport.CurrentTick, position, _stateRev));
            ApplyControl(op, position);
        }

        private void AcceptQueueAdd(int fromPlayer, string trackId, string title, bool playNow)
        {
            if (!_cfg.Sync.AnyoneCanQueue && fromPlayer != RunnerBridge.LocalPlayerId) return;

            if (playNow || string.IsNullOrEmpty(_currentTrackId))
            {
                AuthorisePlay(trackId, title, 0f);
                return;
            }

            _queue.Add(new QueueEntry { TrackId = trackId, Title = title, AddedByPlayerId = fromPlayer });
            _stateRev++;
            _transport.Broadcast(SyncProtocol.QueueAdd(trackId, title, playNow: false));
        }

        private void PlayNextFromQueue()
        {
            if (_queue.Count == 0)
            {
                _stateRev++;
                _transport.Broadcast(SyncProtocol.Control(
                    SyncProtocol.ControlOp.Stop, _transport.CurrentTick, 0f, _stateRev));
                ApplyControl(SyncProtocol.ControlOp.Stop, 0f);
                return;
            }

            var next = _queue[0];
            _queue.RemoveAt(0);
            AuthorisePlay(next.TrackId, next.Title, 0f);
        }

        /// <summary>Called when the local track finishes; only the host advances the queue.</summary>
        public void NotifyTrackEnded()
        {
            if (Active && _transport.IsHost) PlayNextFromQueue();
        }

        // ------------------------------------------------------------------ applying state

        private void ApplyNowPlaying(string trackId, string title, int startTick, float offset)
        {
            _currentTrackId = trackId;
            _currentTitle = title;
            _clock.Schedule(startTick, offset);

            var track = _resolveTrack?.Invoke(trackId);
            if (track == null)
            {
                Log.Info($"cannot play '{title}' - not in local library");
                if (Active && !_transport.IsHost)
                    _transport.SendToHost(SyncProtocol.Need(trackId, SyncProtocol.NeedReason.NotInLibrary));
                if (!string.Equals(_cfg.Sync.OnMissingTrack, "keeplocal", StringComparison.OrdinalIgnoreCase))
                    _stopLocal();
                return;
            }

            // Where the track should be by the time loading finishes. Negative until the
            // scheduled start, which is exactly the loading window.
            float expected = Mathf.Max(0f, _clock.ExpectedPosition(_transport.CurrentTick, _transport.TickRate));
            _playLocal(track, expected);
        }

        private void ApplyControl(SyncProtocol.ControlOp op, float position)
        {
            switch (op)
            {
                case SyncProtocol.ControlOp.Pause:
                    _clock.Pause(position);
                    break;
                case SyncProtocol.ControlOp.Resume:
                    _clock.Resume(_transport.CurrentTick);
                    break;
                case SyncProtocol.ControlOp.Stop:
                    _clock.Clear();
                    _currentTrackId = null;
                    _currentTitle = null;
                    _stopLocal();
                    break;
            }
        }

        // ------------------------------------------------------------------ receiving

        private void HandleFrame(int sender, SyncProtocol.Reader r)
        {
            if (!_cfg.Sync.AcceptFromOthers && sender != RunnerBridge.LocalPlayerId) return;

            Touch(sender);

            switch (r.Type)
            {
                case SyncProtocol.MsgType.Hello:
                {
                    var peer = Peer(sender);
                    peer.Name = r.Str();
                    peer.CanFetchUrls = r.Bool();
                    if (!r.Ok) return;

                    // A new peer needs the current state, and only the host has the
                    // authoritative copy.
                    if (_transport.IsHost && !string.IsNullOrEmpty(_currentTrackId))
                        SendSnapshotTo(sender);
                    break;
                }

                case SyncProtocol.MsgType.NowPlaying:
                {
                    r.U8();                                  // source
                    string id = r.Str();
                    string title = r.Str();
                    int startTick = r.I32();
                    float offset = r.F32();
                    r.F32();                                 // duration
                    int rev = r.I32();
                    if (!r.Ok) return;

                    if (rev <= _stateRev && sender != RunnerBridge.LocalPlayerId) return;
                    _stateRev = rev;
                    ApplyNowPlaying(id, title, startTick, offset);
                    break;
                }

                case SyncProtocol.MsgType.Control:
                {
                    var op = (SyncProtocol.ControlOp)r.U8();
                    r.I32();                                 // atTick
                    float pos = r.F32();
                    int rev = r.I32();
                    if (!r.Ok) return;

                    // A client asking the host to skip is a request, not a state change.
                    if (_transport.IsHost && op == SyncProtocol.ControlOp.SkipNext)
                    {
                        if (_cfg.Sync.AnyoneCanSkip) PlayNextFromQueue();
                        return;
                    }
                    if (_transport.IsHost && (op == SyncProtocol.ControlOp.Pause ||
                                              op == SyncProtocol.ControlOp.Resume))
                    {
                        AuthoriseControl(op, pos);
                        return;
                    }

                    if (rev <= _stateRev) return;
                    _stateRev = rev;
                    ApplyControl(op, pos);
                    break;
                }

                case SyncProtocol.MsgType.QueueAdd:
                {
                    string id = r.Str();
                    string title = r.Str();
                    bool playNow = r.Bool();
                    if (!r.Ok) return;

                    if (_transport.IsHost) AcceptQueueAdd(sender, id, title, playNow);
                    else _queue.Add(new QueueEntry { TrackId = id, Title = title, AddedByPlayerId = sender });
                    break;
                }

                case SyncProtocol.MsgType.Need:
                {
                    string id = r.Str();
                    var reason = (SyncProtocol.NeedReason)r.U8();
                    if (!r.Ok) return;

                    var peer = Peer(sender);
                    peer.MissingTrackId = id;
                    peer.MissingReason = reason;
                    break;
                }

                case SyncProtocol.MsgType.Have:
                {
                    string id = r.Str();
                    if (!r.Ok) return;
                    var peer = Peer(sender);
                    if (peer.MissingTrackId == id) peer.MissingTrackId = null;
                    break;
                }

                case SyncProtocol.MsgType.StateRequest:
                    if (_transport.IsHost) SendSnapshotTo(sender);
                    break;
            }
        }

        private void SendSnapshotTo(int playerId)
        {
            if (string.IsNullOrEmpty(_currentTrackId)) return;

            // A late joiner gets the live position, not the start of the track.
            float pos = _clock.ExpectedPosition(_transport.CurrentTick, _transport.TickRate);
            int leadTicks = Mathf.Max(1, Mathf.RoundToInt(
                _transport.TickRate * (_cfg.Sync.StartLeadMs / 1000f)));

            _transport.SendToPlayer(playerId, SyncProtocol.NowPlaying(
                SyncProtocol.TrackSource.LocalFile, _currentTrackId, _currentTitle,
                _transport.CurrentTick + leadTicks, pos + (_cfg.Sync.StartLeadMs / 1000f),
                0f, _stateRev));
        }

        private PeerState Peer(int id)
        {
            if (!_peers.TryGetValue(id, out var p)) _peers[id] = p = new PeerState();
            return p;
        }

        private void Touch(int id) => Peer(id).LastSeen = Time.realtimeSinceStartup;

        // ------------------------------------------------------------------ per-frame

        public void Tick(AudioSource playback)
        {
            if (!_cfg.Sync.Enabled) return;

            if (!_transport.Available)
            {
                if (_helloSent) Reset();
                return;
            }

            float now = Time.realtimeSinceStartup;

            // Announce ourselves until somebody answers - peers load at different times.
            if (!_helloSent || (_peers.Count == 0 && now >= _nextHello))
            {
                _nextHello = now + 5f;
                _helloSent = true;
                _transport.SendToHost(SyncProtocol.Hello(
                    RunnerBridge.LocalPlayerId.ToString(),
                    _cfg.UrlMode.Enabled));
                if (!_transport.IsHost)
                    _transport.SendToHost(SyncProtocol.StateRequest());
            }

            _clock.Correct(playback, _transport.CurrentTick, _transport.TickRate, now);
        }

        private void Reset()
        {
            _helloSent = false;
            _peers.Clear();
            _queue.Clear();
            _stateRev = 0;
            _currentTrackId = null;
            _currentTitle = null;
            _clock.Clear();
            _transport.Reset();
        }

        public void Dispose() => Reset();
    }
}

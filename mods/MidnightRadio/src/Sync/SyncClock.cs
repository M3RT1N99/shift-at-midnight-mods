using System;
using UnityEngine;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// Keeps every peer at the same position in the same track.
    ///
    /// The shared time base is Fusion's simulation tick. Every peer in a session agrees on
    /// the tick number, so "start at tick T" means the same instant everywhere without us
    /// measuring latency ourselves. Wall-clock time would not work - it is per-machine.
    ///
    /// Correction policy, in three bands. The numbers matter, so here is the reasoning:
    /// each player listens on their own speakers in their own room, so there is no acoustic
    /// interference between the outputs. That makes the requirement far looser than for
    /// co-located speakers, where sub-millisecond alignment matters. What people actually
    /// notice here is one player reacting to a drop before another.
    ///
    ///   < 120 ms      leave it alone. Inaudible as a group experience.
    ///   120 - 750 ms  nudge the pitch by at most 2%. Pulls back over a few seconds and is
    ///                 far less noticeable than a jump. Unity resamples without formant
    ///                 correction, so 2% is about the ceiling before music sounds off-key.
    ///   > 750 ms      hard seek. Audible, but at this error so is doing nothing.
    ///
    /// Drift is real: sound-card clocks run tens of ppm off the game clock, which is
    /// hundreds of milliseconds over a long track. That is why this re-checks continuously
    /// rather than trusting the initial start.
    /// </summary>
    internal sealed class SyncClock
    {
        private readonly Config _cfg;

        private int   _startTick    = -1;
        private float _startOffset;
        private bool  _paused;
        private float _pausedAt;
        private float _lastDriftMs;
        private float _nextCheck;

        public SyncClock(Config cfg) { _cfg = cfg; }

        public bool HasSchedule => _startTick >= 0;
        public float LastDriftMs => _lastDriftMs;

        /// <summary>Schedules playback so that at <paramref name="startTick"/> the track is at <paramref name="startOffsetSec"/>.</summary>
        public void Schedule(int startTick, float startOffsetSec)
        {
            _startTick   = startTick;
            _startOffset = startOffsetSec;
            _paused      = false;
            _lastDriftMs = 0f;
            _nextCheck   = 0f;
        }

        public void Clear()
        {
            _startTick = -1;
            _paused = false;
        }

        public void Pause(float atPosition) { _paused = true; _pausedAt = atPosition; }

        public void Resume(int startTick)
        {
            _startTick   = startTick;
            _startOffset = _pausedAt;
            _paused      = false;
        }

        /// <summary>
        /// Where the track should be right now, in seconds. Negative means the scheduled
        /// start is still in the future - callers use that window to finish loading.
        /// </summary>
        public float ExpectedPosition(int currentTick, float tickRate)
        {
            if (_startTick < 0) return 0f;
            if (_paused) return _pausedAt;
            if (tickRate <= 0f) return _startOffset;

            return _startOffset + (currentTick - _startTick) / tickRate;
        }

        /// <summary>Seconds until the scheduled start; zero or less means it has begun.</summary>
        public float SecondsUntilStart(int currentTick, float tickRate)
        {
            if (_startTick < 0 || tickRate <= 0f) return 0f;
            return (_startTick - currentTick) / tickRate;
        }

        /// <summary>
        /// Compares the AudioSource against the schedule and corrects it. Call every frame;
        /// it throttles itself to the configured interval.
        /// </summary>
        public void Correct(AudioSource src, int currentTick, float tickRate, float now)
        {
            if (src == null) return;
            if (!HasSchedule || _paused)
            {
                if (!Mathf.Approximately(src.pitch, 1f)) src.pitch = 1f;
                return;
            }
            if (!src.isPlaying) return;
            if (now < _nextCheck) return;

            _nextCheck = now + Mathf.Max(1f, _cfg.Sync.ResyncIntervalSeconds);

            float expected = ExpectedPosition(currentTick, tickRate);
            if (expected < 0f) return;

            // Past the end: let the track finish rather than seeking into silence.
            if (src.clip != null && expected >= src.clip.length)
            {
                if (!Mathf.Approximately(src.pitch, 1f)) src.pitch = 1f;
                return;
            }

            float driftSec = src.time - expected;
            _lastDriftMs = driftSec * 1000f;

            float absMs = Mathf.Abs(_lastDriftMs);

            if (absMs < _cfg.Sync.SoftCorrectAboveMs)
            {
                if (!Mathf.Approximately(src.pitch, 1f)) src.pitch = 1f;
                return;
            }

            if (absMs >= _cfg.Sync.HardSeekAboveMs)
            {
                src.pitch = 1f;
                src.time  = Mathf.Clamp(expected, 0f, Math.Max(0f, (src.clip?.length ?? expected) - 0.05f));
                Log.Debug($"hard seek: drift {_lastDriftMs:F0} ms -> {expected:F2}s");
                return;
            }

            // Ahead of schedule slows down, behind speeds up. Scaled by how far off we are
            // so small errors get a gentle nudge and large ones converge faster.
            float range  = Mathf.Max(0.001f, _cfg.Sync.SoftCorrectPitchRange);
            float span   = Mathf.Max(1f, _cfg.Sync.HardSeekAboveMs - _cfg.Sync.SoftCorrectAboveMs);
            float factor = Mathf.Clamp01((absMs - _cfg.Sync.SoftCorrectAboveMs) / span);

            src.pitch = 1f - Mathf.Sign(driftSec) * range * factor;
        }
    }
}

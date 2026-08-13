using System;
using System.Collections;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>
    /// Owns the one decoded clip and applies it to whichever placed radio exists in the
    /// current scene. The vanilla source is restored on every stop/error/teardown path.
    /// </summary>
    internal sealed class PlaybackController : IDisposable
    {
        private readonly Config _config;

        private RadioTarget _target;
        private AudioClip _clip;
        private int _loadGeneration;
        private bool _started;
        private bool _paused;
        private bool _muted;
        private bool _endedReported;
        private bool _observedPlaying;
        private float _detachedPosition;
        private AudioLoader.LoadTicket _activeLoad;

        public PlaybackController(Config config) { _config = config; }

        public TrackInfo CurrentTrack { get; private set; }
        public bool IsLoading { get; private set; }
        public string LastError { get; private set; }
        public bool IsPaused => _paused;
        public bool IsMuted => _muted;
        public float PositionSeconds =>
            _target != null && _target.IsValid && _target.Playback != null
                ? _target.Playback.time
                : 0f;

        public event Action TrackEnded;

        public void SetTarget(RadioTarget target)
        {
            if (ReferenceEquals(_target, target)) return;

            if (_target != null)
            {
                if (_target.IsValid && _target.Playback != null && _clip != null)
                    _detachedPosition = _target.Playback.time;
                _target.Release();
            }

            _target = target;
            if (_target != null && _target.IsValid && _clip != null)
                AttachAndPlay(_detachedPosition);
        }

        public IEnumerator Play(TrackInfo track, float startAtSeconds = 0f)
        {
            if (track == null) yield break;

            int generation = ++_loadGeneration;
            _activeLoad?.Cancel();
            var ticket = new AudioLoader.LoadTicket();
            _activeLoad = ticket;
            IsLoading = true;
            LastError = null;

            var result = new AudioLoader.Result();
            yield return AudioLoader.Load(track.Path, result, ticket);

            if (generation != _loadGeneration)
            {
                if (result.Clip != null) AudioLoader.Unload(result.Clip);
                yield break;
            }

            _activeLoad = null;

            IsLoading = false;
            if (!result.Ok)
            {
                LastError = result.Error ?? "unknown decoder error";
                Log.Warn($"could not load '{track.Title}': {LastError}");
                yield break;
            }

            if (result.Clip.length > _config.Playback.MaxTrackMinutes * 60f)
            {
                LastError = $"track exceeds the configured {_config.Playback.MaxTrackMinutes}-minute limit";
                Log.Warn($"could not load '{track.Title}': {LastError}");
                AudioLoader.Unload(result.Clip);
                yield break;
            }

            DetachClip(restoreVanilla: false);
            _clip = result.Clip;
            CurrentTrack = track;
            _paused = false;
            _started = false;
            _endedReported = false;
            _observedPlaying = false;
            _detachedPosition = 0f;

            if (_target != null && _target.IsValid)
                AttachAndPlay(startAtSeconds);

            Log.Info($"loaded '{track.Title}' ({track.Id.Substring(0, Math.Min(12, track.Id.Length))})");
        }

        public void Tick()
        {
            if (_target == null || !_target.IsValid) return;

            var source = _target.Playback;
            if (_clip != null && source.clip != _clip)
                AttachAndPlay(0f);

            if (_clip == null) return;

            _target.SuppressOriginal(true);
            source.maxDistance = _config.Playback.MaxRangeMeters;
            source.volume = _target.SwitchedOn && !_muted ? _config.Playback.Volume : 0f;

            if (source.isPlaying) _observedPlaying = true;

            if (_started && _observedPlaying && !_paused && !source.isPlaying && !_endedReported)
            {
                _endedReported = true;
                TrackEnded?.Invoke();
            }
        }

        public void TogglePause()
        {
            if (_clip == null || _target == null || !_target.IsValid) return;
            if (_paused)
            {
                _target.Playback.UnPause();
                _paused = false;
            }
            else
            {
                _target.Playback.Pause();
                _paused = true;
            }
        }

        public void SetMuted(bool muted) => _muted = muted;

        public void Seek(float seconds)
        {
            if (_clip == null || _target == null || !_target.IsValid) return;
            _target.Playback.time = Mathf.Clamp(seconds, 0f, Math.Max(0f, _clip.length - 0.05f));
            _endedReported = false;
            _observedPlaying = false;
            _detachedPosition = 0f;
        }

        public void Stop()
        {
            ++_loadGeneration;
            _activeLoad?.Cancel();
            _activeLoad = null;
            IsLoading = false;
            LastError = null;
            DetachClip(restoreVanilla: true);
            CurrentTrack = null;
            _paused = false;
            _started = false;
            _endedReported = false;
            _observedPlaying = false;
        }

        private void AttachAndPlay(float startAtSeconds)
        {
            var source = _target.Playback;
            source.Stop();
            source.clip = _clip;
            source.loop = false;
            source.pitch = 1f;
            source.time = Mathf.Clamp(startAtSeconds, 0f, Math.Max(0f, _clip.length - 0.05f));
            source.volume = 0f;
            _target.SuppressOriginal(true);
            source.Play();
            if (_paused) source.Pause();
            _started = true;
            _endedReported = false;
            _observedPlaying = source.isPlaying;
        }

        private void DetachClip(bool restoreVanilla)
        {
            if (_target != null && _target.IsValid)
            {
                _target.Playback.Stop();
                _target.Playback.clip = null;
                if (restoreVanilla) _target.SuppressOriginal(false);
            }

            if (_clip != null)
            {
                AudioLoader.Unload(_clip);
                _clip = null;
            }
        }

        public void Dispose()
        {
            Stop();
            if (_target != null) _target.Release();
            _target = null;
        }
    }
}

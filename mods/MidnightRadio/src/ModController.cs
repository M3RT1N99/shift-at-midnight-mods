using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>Wires config, library, radio adoption, playback, URL acquisition and UI.</summary>
    internal sealed class ModController : IDisposable
    {
        private readonly string _dataDirectory;
        private readonly string _configPath;
        private readonly Config _config;
        private readonly MusicLibrary _library;
        private readonly PlaybackController _player;
        private readonly YtDlpBridge _ytDlp;
        private readonly RadioUI _ui;
        private readonly CancellationTokenSource _shutdown = new();

        private readonly Sync.SyncTransport _transport;
        private readonly Sync.SyncClock _clock;
        private readonly Sync.SyncSession _session;

        private RadioTarget _radio;
        private float _nextResolve;
        private int _currentIndex = -1;
        private bool _reloadRunning;
        private bool _reloadPending;
        private bool _downloadRunning;
        private bool _disposed;

        public ModController()
        {
            _dataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "MidnightRadio");
            _configPath = Path.Combine(_dataDirectory, "config.json");
            Directory.CreateDirectory(_dataDirectory);

            SeedDefaultConfig();
            _config = Config.Load(_configPath);
            Log.DebugEnabled = string.Equals(
                _config.Logging.Level, "debug", StringComparison.OrdinalIgnoreCase);

            _library = new MusicLibrary(_dataDirectory, _config);
            _player = new PlaybackController(_config);
            _player.TrackEnded += AutoNext;
            _ytDlp = new YtDlpBridge(_config, _dataDirectory);

            // Synced playback rides on the game's own Fusion connection. With no session,
            // no receive path or no other modded peer, every one of these calls degrades to
            // plain local playback - the session decides, the controller does not branch.
            _transport = new Sync.SyncTransport(_config);
            _clock = new Sync.SyncClock(_config);
            _session = new Sync.SyncSession(
                _config,
                _transport,
                _clock,
                ResolveTrackById,
                (track, offset) => MelonCoroutines.Start(_player.Play(track, offset)),
                () => _player.Stop());

            // Until this succeeds RunnerBridge stays not-receive-ready, which keeps sending
            // disabled too. A client that transmits but cannot hear replies would look
            // synced while silently drifting, so the two are gated together.
            if (_config.Sync.Enabled) Sync.ReceiveHook.Apply(_transport);

            _ui = new RadioUI(
                _config,
                _library,
                _player,
                Play,
                ReloadLibrary,
                Next,
                Previous,
                DownloadUrl,
                SaveConfig);

            ReloadLibrary();
        }

        public void Update()
        {
            if (_disposed) return;
            MainThread.Drain();

            float now = Time.realtimeSinceStartup;
            if ((_radio == null || !_radio.IsValid) && now >= _nextResolve)
            {
                _nextResolve = now + 2f;
                ResolveRadio();
            }

            _player.Tick();
            _session.Tick(_radio != null && _radio.IsValid ? _radio.Playback : null);
            _ui.Tick();
        }

        public void Draw() => _ui.Draw();

        public void SceneChanged()
        {
            _ui.Close();
            // A scene transition invalidates both the placed object and any in-flight
            // file load. Stop explicitly so a later boombox cannot restart an old track
            // from the beginning without a new user action.
            _player.Stop();
            _player.SetTarget(null);
            _radio = null;
            _nextResolve = 0f;
        }

        private void ResolveRadio()
        {
            Log.Guard("resolve radio", () =>
            {
                var found = RadioTarget.TryResolve();
                if (found == null) return;
                _radio = found;
                _player.SetTarget(found);
                _ui.SetStatus("Radio gefunden.");
                Log.Info("adopted placed boombox audio source");
            });
        }

        private void Play(TrackInfo track)
        {
            if (track == null) return;
            var snapshot = _library.Snapshot;
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (string.Equals(snapshot[i].Id, track.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _currentIndex = i;
                    break;
                }
            }

            // Goes through the session so every player hears it. Falls back to a direct
            // local play when there is nobody to sync with.
            _session.RequestPlay(track);
        }

        private TrackInfo ResolveTrackById(string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return null;
            var tracks = _library.Snapshot;
            int index = FindTrackIndex(tracks, trackId);
            return index >= 0 ? tracks[index] : null;
        }

        private void Next()
        {
            var tracks = _library.Snapshot;
            if (tracks.Count == 0) return;
            int current = FindCurrentIndex(tracks);
            if (_config.Playback.Shuffle && tracks.Count > 1)
            {
                if (current < 0)
                    _currentIndex = System.Random.Shared.Next(tracks.Count);
                else
                {
                    int next = System.Random.Shared.Next(tracks.Count - 1);
                    _currentIndex = next >= current ? next + 1 : next;
                }
            }
            else
            {
                _currentIndex = (current + 1 + tracks.Count) % tracks.Count;
            }
            Play(tracks[_currentIndex]);
        }

        private void Previous()
        {
            var tracks = _library.Snapshot;
            if (tracks.Count == 0) return;
            int current = FindCurrentIndex(tracks);
            _currentIndex = current <= 0 ? tracks.Count - 1 : current - 1;
            Play(tracks[_currentIndex]);
        }

        private void AutoNext()
        {
            // In a synced session only the host advances the queue, otherwise every client
            // would pick its own "next" and the lobby would scatter.
            if (_session.Active)
            {
                _session.NotifyTrackEnded();
                return;
            }

            var tracks = _library.Snapshot;
            if (tracks.Count == 0) return;

            string repeat = (_config.Playback.Repeat ?? "all").Trim().ToLowerInvariant();
            int current = FindCurrentIndex(tracks);
            if (repeat == "one" && current >= 0)
            {
                Play(tracks[current]);
                return;
            }
            if ((repeat == "none" || repeat == "off") && current >= tracks.Count - 1)
            {
                _player.Stop();
                return;
            }

            Next();
        }

        private int FindCurrentIndex(IReadOnlyList<TrackInfo> tracks)
        {
            int byId = FindTrackIndex(tracks, _player.CurrentTrack?.Id);
            if (byId >= 0) return byId;
            return _currentIndex >= 0 && _currentIndex < tracks.Count ? _currentIndex : -1;
        }

        private static int FindTrackIndex(IReadOnlyList<TrackInfo> tracks, string trackId)
        {
            if (string.IsNullOrEmpty(trackId)) return -1;
            for (int i = 0; i < tracks.Count; i++)
                if (string.Equals(tracks[i].Id, trackId, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private void ReloadLibrary()
        {
            if (_reloadRunning)
            {
                _reloadPending = true;
                return;
            }
            _reloadRunning = true;
            _ui?.SetStatus("Musikbibliothek wird gelesen …");

            Task.Run(() => _library.Reload()).ContinueWith((Task<IReadOnlyList<TrackInfo>> task) => Post(() =>
            {
                _reloadRunning = false;
                if (task.IsFaulted)
                    _ui.SetStatus("Bibliothek konnte nicht gelesen werden.");
                else
                    _ui.SetStatus($"{_library.Count} Titel gefunden.");

                // Remap while CurrentTrack still carries the content identity; keeping an
                // old numeric index after a resort would repeat or skip a title.
                _currentIndex = FindTrackIndex(_library.Snapshot, _player.CurrentTrack?.Id);
                if (_reloadPending)
                {
                    _reloadPending = false;
                    ReloadLibrary();
                }
            }));
        }

        private void DownloadUrl(string url)
        {
            if (_downloadRunning)
            {
                _ui.SetStatus("Ein Download läuft bereits.");
                return;
            }

            _downloadRunning = true;
            _ui.SetStatus("yt-dlp wird gestartet …");
            var progress = new Progress<DownloadProgress>(p => Post(() =>
            {
                if (p.Percent.HasValue)
                    _ui.SetStatus($"Download {p.Percent.Value:0}% …");
            }));

            _ytDlp.DownloadAsync(url, progress, _shutdown.Token).ContinueWith((Task<DownloadResult> task) => Post(() =>
            {
                _downloadRunning = false;
                if (task.IsCanceled)
                {
                    _ui.SetStatus("Download abgebrochen.");
                    return;
                }
                if (task.IsFaulted)
                {
                    _ui.SetStatus("Downloadfehler: " + task.Exception?.GetBaseException().Message);
                    return;
                }

                DownloadResult result = task.Result;
                if (!result.Success)
                {
                    _ui.SetStatus(result.ErrorCode + ": " + result.Message);
                    return;
                }

                _ui.SetStatus(result.Message + " Bibliothek wird aktualisiert …");
                ReloadLibrary();
            }));
        }

        private void Post(Action action)
        {
            if (action == null || _disposed) return;
            MainThread.Post(action);
        }

        private void SeedDefaultConfig()
        {
            if (File.Exists(_configPath)) return;
            string template = Path.Combine(_dataDirectory, "config.json.default");
            if (File.Exists(template)) File.Copy(template, _configPath, overwrite: false);
        }

        private void SaveConfig() => _config.Save(_configPath);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            _shutdown.Dispose();
            _ui.Close();
            Sync.ReceiveHook.Remove();
            _session.Dispose();
            _player.Dispose();
            SaveConfig();
            MainThread.Clear();
        }
    }
}

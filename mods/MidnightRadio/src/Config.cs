using System;
using System.Collections.Generic;
using System.IO;

namespace MidnightRadio
{
    /// <summary>
    /// Mirrors UserData/MidnightRadio/config.json. Every tuning value the mod uses lives
    /// here - no magic numbers buried in logic. Loading never throws: a broken or missing
    /// file falls back to defaults and logs, because a config typo must not stop the game
    /// from starting.
    /// </summary>
    internal sealed class Config
    {
        /// <summary>Bump when a stored config needs repairing; see <see cref="Migrate"/>.</summary>
        public const int CurrentVersion = 4;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>True when loading repaired this config and it should be written back.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Migrated { get; private set; }
        public string Hotkey { get; set; } = "F4";
        public string Language { get; set; } = "auto";
        public List<string> MusicDirs { get; set; } = new List<string>();

        public PlaybackCfg Playback { get; set; } = new PlaybackCfg();
        public SyncCfg     Sync     { get; set; } = new SyncCfg();
        public UrlCfg      UrlMode  { get; set; } = new UrlCfg();
        public ToolsCfg    Tools    { get; set; } = new ToolsCfg();
        public CacheCfg    Cache    { get; set; } = new CacheCfg();
        public LogCfg      Logging  { get; set; } = new LogCfg();

        internal sealed class PlaybackCfg
        {
            /// <summary>Deliberately quiet by default - a fresh install must not blast the lobby.</summary>
            public float Volume { get; set; } = 0.15f;
            public float VolumeCeiling { get; set; } = 0.85f;
            public float MaxRangeMeters { get; set; } = 50f;
            public bool  Shuffle { get; set; }
            public string Repeat { get; set; } = "all";
            public float DuckWhileTransmittingDb { get; set; } = -12f;
            public int   MaxTrackMinutes { get; set; } = 10;
        }

        internal sealed class SyncCfg
        {
            // Synchronised playback is the point of the mod: everyone hears the same track
            // and anyone may queue.
            //
            // Safety comes from the runtime gate in SyncTransport.CanSend(), which verifies
            // the session actually permits reliable data and falls back to local playback
            // if not - not from shipping the switch off.
            //
            // It did ship off for one release while the receive hook was unproven, but that made
            // the feature unreachable in practice: both players install the same package,
            // so both got it disabled, and "everyone hears the same music" never happened
            // for anyone who did not hand-edit a file.
            //
            // The risk it was guarding against is now much smaller - the hook is applied
            // only once a session is actually running, never during load - and the failure
            // mode is recoverable: set this to false and playback returns to local.
            public bool Enabled { get; set; } = true;
            public int  ProtocolVersion { get; set; } = 1;

            public bool AcceptFromOthers { get; set; } = true;
            public bool AnyoneCanQueue { get; set; } = true;
            public bool AnyoneCanSkip { get; set; } = true;
            public bool RequireVoteToSkip { get; set; }

            /// <summary>Lead time before a scheduled start, so every peer can finish loading.</summary>
            public int StartLeadMs { get; set; } = 1500;
            public int PrefetchLeadSeconds { get; set; } = 20;
            public float ResyncIntervalSeconds { get; set; } = 15f;

            public float SoftCorrectAboveMs { get; set; } = 120f;
            public float SoftCorrectPitchRange { get; set; } = 0.02f;
            public float HardSeekAboveMs { get; set; } = 750f;

            public string OnMissingTrack { get; set; } = "silent";
            public string OnHuntMusic { get; set; } = "duck";

            public List<string> BlockedPlayers { get; set; } = new List<string>();
            public PeerTransferCfg PeerToPeerTransfer { get; set; } = new PeerTransferCfg();
        }

        internal sealed class PeerTransferCfg
        {
            public bool Enabled { get; set; }
            public int ConsentAcceptedVersion { get; set; }
            public string AcceptedAt { get; set; }
            public int MaxFileMB { get; set; } = 25;
            public int MaxRateKBps { get; set; } = 96;
            public bool OnlyOutsideHunt { get; set; } = true;
        }

        internal sealed class UrlCfg
        {
            public bool Enabled { get; set; }
            public int  NoticeAcceptedVersion { get; set; }
            public string AcceptedAt { get; set; }
            public string AudioFormat { get; set; } = "vorbis";
            public int  AudioQuality { get; set; }
            public bool AllowPlaylists { get; set; }
            public int  MaxDurationMinutes { get; set; } = 15;
            public int  ResolveTimeoutSeconds { get; set; } = 120;
        }

        internal sealed class ToolsCfg
        {
            public string YtDlpPath { get; set; }
            public string FfmpegPath { get; set; }

            /// <summary>Check for newer tools on start. yt-dlp breaks quickly when stale.</summary>
            public bool AutoUpdate { get; set; } = true;

            /// <summary>
            /// ffmpeg has no cheap version endpoint and its build is ~80 MB, so it is
            /// re-fetched on an interval rather than checked every start.
            /// </summary>
            public int FfmpegRefreshDays { get; set; } = 30;
        }

        internal sealed class CacheCfg
        {
            public int  MaxCacheMB { get; set; } = 2048;
            public bool EvictionEnabled { get; set; } = true;
        }

        internal sealed class LogCfg
        {
            public string Level { get; set; } = "info";
            public int  KeepDays { get; set; } = 7;
            public bool SyncDiagnostics { get; set; }
        }

        public static Config Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Log.Info($"no config at {path}, using defaults");
                    return new Config();
                }

                var json = File.ReadAllText(path);
                var cfg  = System.Text.Json.JsonSerializer.Deserialize<Config>(
                    json, new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                if (cfg == null)
                {
                    Log.Warn("config could not be parsed, using defaults");
                    return new Config();
                }

                cfg.FillMissingSections();
                cfg.Migrate();
                cfg.Clamp();
                return cfg;
            }
            catch (Exception ex)
            {
                Log.Warn($"config load failed ({ex.Message}), using defaults");
                return new Config();
            }
        }

        /// <summary>Writes UTF-8 JSON via a same-directory temporary file.</summary>
        public bool Save(string path)
        {
            try
            {
                FillMissingSections();
                Clamp();
                var parent = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                var json = System.Text.Json.JsonSerializer.Serialize(this,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, json + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));
                File.Move(temporary, path, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"config save failed ({ex.Message})");
                return false;
            }
        }

        /// <summary>
        /// Brings an older config forward. Defaults only apply to files that do not exist
        /// yet, so a stored config keeps whatever was written when it was created - which
        /// is how an early build's local-only settings survived into a version meant to be
        /// synchronised. Version 3 repairs that.
        /// </summary>
        private void Migrate()
        {
            if (Version >= CurrentVersion) return;

            int from = Version;
            Version = CurrentVersion;
            Migrated = true;

            if (from < 3)
            {
                // Synced playback with an open queue is the point of the mod; an older file
                // may carry it disabled. Safety comes from the runtime gate in
                // SyncTransport.CanSend(), not from these flags.
                Sync.Enabled = true;
                Sync.AcceptFromOthers = true;
                Sync.AnyoneCanQueue = true;
                Sync.AnyoneCanSkip = true;

                // A stored zero means the radio would be silent with no indication why.
                if (Playback.Volume <= 0f) Playback.Volume = 0.15f;

                Log.Info($"config migrated from version {from} to {CurrentVersion}: "
                         + "synced playback re-enabled");
            }

            if (from < 4)
            {
                // Version 3 shipped with sync off while the receive hook was unproven. Both
                // players install the same package, so both ended up disabled and the
                // feature was unreachable without editing a file by hand.
                Sync.Enabled = true;
                Sync.AcceptFromOthers = true;
                Sync.AnyoneCanQueue = true;
                Sync.AnyoneCanSkip = true;

                Log.Info("config migrated to version 4: synced playback enabled by default");
            }
        }

        private void FillMissingSections()
        {
            MusicDirs ??= new List<string>();
            Playback ??= new PlaybackCfg();
            Sync ??= new SyncCfg();
            Sync.BlockedPlayers ??= new List<string>();
            Sync.PeerToPeerTransfer ??= new PeerTransferCfg();
            UrlMode ??= new UrlCfg();
            Tools ??= new ToolsCfg();
            Cache ??= new CacheCfg();
            Logging ??= new LogCfg();
        }

        /// <summary>Keeps hand-edited values inside sane bounds instead of trusting them.</summary>
        public void Clamp()
        {
            FillMissingSections();
            Playback.VolumeCeiling = Math.Min(Math.Max(Playback.VolumeCeiling, 0.05f), 1f);
            Playback.Volume        = Math.Min(Math.Max(Playback.Volume, 0f), Playback.VolumeCeiling);
            Playback.MaxRangeMeters = Math.Min(Math.Max(Playback.MaxRangeMeters, 1f), 250f);
            Playback.MaxTrackMinutes = Math.Min(Math.Max(Playback.MaxTrackMinutes, 1), 24 * 60);
            string repeat = (Playback.Repeat ?? "all").Trim().ToLowerInvariant();
            Playback.Repeat = repeat == "one" || repeat == "none" || repeat == "off"
                ? repeat
                : "all";

            Sync.SoftCorrectAboveMs    = Math.Max(20f, Sync.SoftCorrectAboveMs);
            Sync.HardSeekAboveMs       = Math.Max(Sync.SoftCorrectAboveMs + 50f, Sync.HardSeekAboveMs);
            Sync.SoftCorrectPitchRange = Math.Min(Math.Max(Sync.SoftCorrectPitchRange, 0.001f), 0.05f);
            Sync.ResyncIntervalSeconds = Math.Max(1f, Sync.ResyncIntervalSeconds);
            Sync.StartLeadMs           = Math.Min(Math.Max(Sync.StartLeadMs, 250), 10000);
            Sync.PrefetchLeadSeconds   = Math.Min(Math.Max(Sync.PrefetchLeadSeconds, 1), 120);
            Sync.PeerToPeerTransfer.MaxFileMB = Math.Min(
                Math.Max(Sync.PeerToPeerTransfer.MaxFileMB, 1), 1024);
            Sync.PeerToPeerTransfer.MaxRateKBps = Math.Min(
                Math.Max(Sync.PeerToPeerTransfer.MaxRateKBps, 16), 1024 * 10);

            UrlMode.MaxDurationMinutes = Math.Min(Math.Max(UrlMode.MaxDurationMinutes, 1), 24 * 60);
            UrlMode.ResolveTimeoutSeconds = Math.Min(Math.Max(UrlMode.ResolveTimeoutSeconds, 10), 900);
            Cache.MaxCacheMB = Math.Min(Math.Max(Cache.MaxCacheMB, 64), 1024 * 100);
            Logging.KeepDays = Math.Min(Math.Max(Logging.KeepDays, 1), 365);
        }
    }
}

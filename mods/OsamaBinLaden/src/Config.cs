using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OsamaBinLaden
{
    /// <summary>Bounded settings stored under UserData/OsamaBinLaden/config.json.</summary>
    internal sealed class Config
    {
        public const int CurrentVersion = 2;

        public int Version { get; set; } = CurrentVersion;
        public bool Enabled { get; set; } = true;

        /// <summary>Forces the mod to behave like the original single-player-only release,
        /// even when a live multiplayer session is positively confirmed. Kept in sync with
        /// <see cref="SafetyCfg.DisableInMultiplayer"/> by <see cref="Clamp"/>.</summary>
        public bool SinglePlayerOnly { get; set; }
        public SpawnCfg Spawn { get; set; } = new SpawnCfg();
        public AttackCfg Attack { get; set; } = new AttackCfg();
        public EffectsCfg Effects { get; set; } = new EffectsCfg();
        public SafetyCfg Safety { get; set; } = new SafetyCfg();
        public LoggingCfg Logging { get; set; } = new LoggingCfg();
        public MultiplayerCfg Multiplayer { get; set; } = new MultiplayerCfg();

        internal sealed class SpawnCfg
        {
            public bool Enabled { get; set; } = true;
            public int MaximumActive { get; set; } = 1;
            public float ChancePerEligibleEncounter { get; set; } = 1f;
            public float MinimumSpawnDistanceMeters { get; set; } = 10f;
            public float MaximumSpawnDistanceMeters { get; set; } = 30f;
            public float MaximumLifetimeSeconds { get; set; } = 35f;
        }

        internal sealed class AttackCfg
        {
            public float RunSpeedMetersPerSecond { get; set; } = 6f;
            public float DetonationDistanceMeters { get; set; } = 1.75f;
            public float FuseSeconds { get; set; } = 0.35f;
        }

        internal sealed class EffectsCfg
        {
            public bool ScreamEnabled { get; set; } = true;
            public float ScreamVolume { get; set; } = 0.8f;
            public float ExplosionRadiusMeters { get; set; } = 4f;
            public float ExplosionDamage { get; set; } = 100f;
            public float VisualScale { get; set; } = 1f;
        }

        internal sealed class SafetyCfg
        {
            /// <summary>Same opt-out as <see cref="Config.SinglePlayerOnly"/>; either one being
            /// true forces the other true. Two names exist because both predate this mod's
            /// multiplayer support and neither should quietly stop working.</summary>
            public bool DisableInMultiplayer { get; set; }

            /// <summary>The actual permission gate consulted before any Fusion send. False
            /// means the mod may detect a multiplayer session but must never put a single byte
            /// on the wire - a genuine, honoured "stay offline" switch, independent of
            /// <see cref="DisableInMultiplayer"/>.</summary>
            public bool AllowNetworkSends { get; set; } = true;
        }

        internal sealed class LoggingCfg
        {
            public string Level { get; set; } = "info";
        }

        internal sealed class MultiplayerCfg
        {
            /// <summary>How long a peer has to answer Hello with a matching Ready before the
            /// host drops the attempt and requires a fresh handshake.</summary>
            public float HandshakeTimeoutSeconds { get; set; } = 8f;

            /// <summary>How often the host reassures every validated peer that its encounter
            /// session is still alive, independently of Fusion's own connection state.</summary>
            public float HeartbeatIntervalSeconds { get; set; } = 4f;

            /// <summary>How long a client will wait without hearing anything from the host
            /// (Heartbeat included) before resetting its handshake and dropping its mirror.</summary>
            public float PeerTimeoutSeconds { get; set; } = 15f;

            /// <summary>How often the host re-publishes its lobby support marker, so a player
            /// who joins mid-session still discovers that the host runs this mod.</summary>
            public float HostMarkerRepublishSeconds { get; set; } = 5f;
        }

        public static Config Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return new Config();

                Config config = JsonSerializer.Deserialize<Config>(
                    File.ReadAllText(path),
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });

                if (config == null)
                {
                    Log.Warn("config parsed as null; using safe defaults");
                    return new Config();
                }

                config.Clamp();
                return config;
            }
            catch (Exception ex)
            {
                Log.Warn($"config load failed ({ex.Message}); using safe defaults");
                return new Config();
            }
        }

        public bool Save(string path)
        {
            try
            {
                Clamp();
                string fullPath = Path.GetFullPath(path);
                string parent = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

                string temporary = fullPath + ".tmp";
                string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(temporary, json + Environment.NewLine, new UTF8Encoding(false));
                File.Move(temporary, fullPath, true);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"config save failed ({ex.Message})");
                return false;
            }
        }

        public void Clamp()
        {
            Spawn ??= new SpawnCfg();
            Attack ??= new AttackCfg();
            Effects ??= new EffectsCfg();
            Safety ??= new SafetyCfg();
            Logging ??= new LoggingCfg();
            Multiplayer ??= new MultiplayerCfg();

            Version = CurrentVersion;

            // SinglePlayerOnly and Safety.DisableInMultiplayer are two names for one opt-out:
            // either being true forces the mod back to its original single-player-only
            // behaviour. Keep them mirrored so the saved file can never show them disagreeing.
            bool forcedSinglePlayerOnly = SinglePlayerOnly || Safety.DisableInMultiplayer;
            SinglePlayerOnly = forcedSinglePlayerOnly;
            Safety.DisableInMultiplayer = forcedSinglePlayerOnly;
            // Safety.AllowNetworkSends is intentionally left as loaded/set: it is the real,
            // user-tunable permission gate checked before every Fusion send.

            Spawn.MaximumActive = 1;
            Spawn.ChancePerEligibleEncounter = Math.Clamp(Spawn.ChancePerEligibleEncounter, 0f, 1f);
            Spawn.MinimumSpawnDistanceMeters = Math.Clamp(Spawn.MinimumSpawnDistanceMeters, 3f, 60f);
            Spawn.MaximumSpawnDistanceMeters = Math.Clamp(
                Spawn.MaximumSpawnDistanceMeters,
                Spawn.MinimumSpawnDistanceMeters,
                100f);
            Spawn.MaximumLifetimeSeconds = Math.Clamp(Spawn.MaximumLifetimeSeconds, 5f, 180f);

            Attack.RunSpeedMetersPerSecond = Math.Clamp(Attack.RunSpeedMetersPerSecond, 1f, 20f);
            Attack.DetonationDistanceMeters = Math.Clamp(Attack.DetonationDistanceMeters, 0.5f, 8f);
            Attack.FuseSeconds = Math.Clamp(Attack.FuseSeconds, 0f, 5f);

            Effects.ScreamVolume = Math.Clamp(Effects.ScreamVolume, 0f, 1f);
            Effects.ExplosionRadiusMeters = Math.Clamp(Effects.ExplosionRadiusMeters, 1f, 20f);
            Effects.ExplosionDamage = Math.Clamp(Effects.ExplosionDamage, 0f, 500f);
            Effects.VisualScale = Math.Clamp(Effects.VisualScale, 0.5f, 3f);
            Logging.Level = string.Equals(Logging.Level, "debug", StringComparison.OrdinalIgnoreCase)
                ? "debug"
                : "info";

            Multiplayer.HandshakeTimeoutSeconds = Math.Clamp(Multiplayer.HandshakeTimeoutSeconds, 2f, 30f);
            Multiplayer.HeartbeatIntervalSeconds = Math.Clamp(Multiplayer.HeartbeatIntervalSeconds, 1f, 15f);
            Multiplayer.PeerTimeoutSeconds = Math.Clamp(Multiplayer.PeerTimeoutSeconds, 5f, 60f);
            Multiplayer.HostMarkerRepublishSeconds = Math.Clamp(Multiplayer.HostMarkerRepublishSeconds, 1f, 30f);
        }
    }
}

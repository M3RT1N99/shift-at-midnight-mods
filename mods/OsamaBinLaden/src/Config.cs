using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace OsamaBinLaden
{
    /// <summary>Bounded settings stored under UserData/OsamaBinLaden/config.json.</summary>
    internal sealed class Config
    {
        public const int CurrentVersion = 1;

        public int Version { get; set; } = CurrentVersion;
        public bool Enabled { get; set; } = true;
        public bool SinglePlayerOnly { get; set; } = true;
        public SpawnCfg Spawn { get; set; } = new SpawnCfg();
        public AttackCfg Attack { get; set; } = new AttackCfg();
        public EffectsCfg Effects { get; set; } = new EffectsCfg();
        public SafetyCfg Safety { get; set; } = new SafetyCfg();
        public LoggingCfg Logging { get; set; } = new LoggingCfg();

        internal sealed class SpawnCfg
        {
            public bool Enabled { get; set; } = true;
            public int MaximumActive { get; set; } = 1;
            public float ChancePerEligibleEncounter { get; set; } = 1f;
            public float MinimumSpawnDistanceMeters { get; set; } = 10f;
            public float MaximumLifetimeSeconds { get; set; } = 35f;
        }

        internal sealed class AttackCfg
        {
            public float TriggerDistanceMeters { get; set; } = 30f;
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
            public bool ScreenShake { get; set; }
            public float VisualScale { get; set; } = 1f;
        }

        internal sealed class SafetyCfg
        {
            public bool DisableInMultiplayer { get; set; } = true;
            public bool AllowNetworkSends { get; set; }
        }

        internal sealed class LoggingCfg
        {
            public string Level { get; set; } = "info";
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

            Version = CurrentVersion;
            // These invariants are policy, not user-tunable feature flags.
            SinglePlayerOnly = true;
            Safety.DisableInMultiplayer = true;
            Safety.AllowNetworkSends = false;

            Spawn.MaximumActive = 1;
            Spawn.ChancePerEligibleEncounter = Math.Clamp(Spawn.ChancePerEligibleEncounter, 0f, 1f);
            Spawn.MinimumSpawnDistanceMeters = Math.Clamp(Spawn.MinimumSpawnDistanceMeters, 3f, 60f);
            Spawn.MaximumLifetimeSeconds = Math.Clamp(Spawn.MaximumLifetimeSeconds, 5f, 180f);

            Attack.TriggerDistanceMeters = Math.Clamp(Attack.TriggerDistanceMeters, 3f, 100f);
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
        }
    }
}

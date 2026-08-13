using System;
using System.Collections.Generic;
using System.IO;
using OsamaBinLaden;

internal static class Program
{
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "OsamaBinLaden-SmokeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);

        try
        {
            Run("Config missing/malformed JSON uses safe defaults", () => TestSafeDefaults(temporaryRoot));
            Run("Config repairs null sections", () => TestNullSections(temporaryRoot));
            Run("Config clamps values and enforces offline policy", TestClampAndPolicy);
            Run("Config save is atomic and round-trips", () => TestSaveRoundTrip(temporaryRoot));
            Run("ExplosionMath full damage inside trigger", TestFullDamage);
            Run("ExplosionMath uses linear falloff", TestLinearFalloff);
            Run("ExplosionMath returns zero outside radius", TestOutsideRadius);
            Run("ExplosionMath rejects invalid values", TestInvalidExplosionInputs);
        }
        finally
        {
            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch (Exception ex)
            {
                Failures.Add("temporary-directory cleanup: " + ex.Message);
            }
        }

        if (Failures.Count == 0)
        {
            Console.WriteLine("PASS: all 8 smoke tests passed");
            return 0;
        }

        Console.Error.WriteLine($"FAIL: {Failures.Count} smoke test(s) failed");
        foreach (string failure in Failures)
            Console.Error.WriteLine("  - " + failure);
        return 1;
    }

    private static void TestSafeDefaults(string root)
    {
        string missingPath = Path.Combine(root, "missing", "config.json");
        Config missing = Config.Load(missingPath);
        AssertSafePolicy(missing);
        Equal(true, missing.Enabled, "missing config: Enabled");
        Equal(1, missing.Spawn.MaximumActive, "missing config: MaximumActive");
        Near(6f, missing.Attack.RunSpeedMetersPerSecond, "missing config: run speed");

        string malformedPath = Path.Combine(root, "malformed.json");
        File.WriteAllText(malformedPath, "{ this is not valid JSON");
        Config malformed = Config.Load(malformedPath);
        AssertSafePolicy(malformed);
        Equal(true, malformed.Spawn.Enabled, "malformed config: spawn enabled");
        Near(100f, malformed.Effects.ExplosionDamage, "malformed config: damage");
    }

    private static void TestNullSections(string root)
    {
        string path = Path.Combine(root, "null-sections.json");
        File.WriteAllText(
            path,
            "{\"singlePlayerOnly\":false,\"spawn\":null,\"attack\":null," +
            "\"effects\":null,\"safety\":null,\"logging\":null}");

        Config config = Config.Load(path);

        NotNull(config.Spawn, "Spawn section");
        NotNull(config.Attack, "Attack section");
        NotNull(config.Effects, "Effects section");
        NotNull(config.Safety, "Safety section");
        NotNull(config.Logging, "Logging section");
        AssertSafePolicy(config);
        Near(35f, config.Spawn.MaximumLifetimeSeconds, "repaired spawn defaults");
        Near(4f, config.Effects.ExplosionRadiusMeters, "repaired effects defaults");
    }

    private static void TestClampAndPolicy()
    {
        Config config = new Config
        {
            Version = -50,
            SinglePlayerOnly = false,
            Spawn = new Config.SpawnCfg
            {
                MaximumActive = 99,
                ChancePerEligibleEncounter = -2f,
                MinimumSpawnDistanceMeters = 999f,
                MaximumSpawnDistanceMeters = -10f,
                MaximumLifetimeSeconds = 0f
            },
            Attack = new Config.AttackCfg
            {
                RunSpeedMetersPerSecond = 99f,
                DetonationDistanceMeters = 99f,
                FuseSeconds = -1f
            },
            Effects = new Config.EffectsCfg
            {
                ScreamVolume = 9f,
                ExplosionRadiusMeters = 0f,
                ExplosionDamage = 900f,
                VisualScale = 0.1f
            },
            Safety = new Config.SafetyCfg
            {
                DisableInMultiplayer = false,
                AllowNetworkSends = true
            },
            Logging = new Config.LoggingCfg { Level = "trace" }
        };

        config.Clamp();

        Equal(Config.CurrentVersion, config.Version, "Version");
        Equal(1, config.Spawn.MaximumActive, "MaximumActive");
        Near(0f, config.Spawn.ChancePerEligibleEncounter, "Chance lower bound");
        Near(60f, config.Spawn.MinimumSpawnDistanceMeters, "spawn distance upper bound");
        Near(60f, config.Spawn.MaximumSpawnDistanceMeters, "max spawn distance follows minimum");
        Near(5f, config.Spawn.MaximumLifetimeSeconds, "lifetime lower bound");
        Near(20f, config.Attack.RunSpeedMetersPerSecond, "speed upper bound");
        Near(8f, config.Attack.DetonationDistanceMeters, "detonation upper bound");
        Near(0f, config.Attack.FuseSeconds, "fuse lower bound");
        Near(1f, config.Effects.ScreamVolume, "volume upper bound");
        Near(1f, config.Effects.ExplosionRadiusMeters, "radius lower bound");
        Near(500f, config.Effects.ExplosionDamage, "damage upper bound");
        Near(0.5f, config.Effects.VisualScale, "visual scale lower bound");
        Equal("info", config.Logging.Level, "logging allow-list");
        AssertSafePolicy(config);
    }

    private static void TestSaveRoundTrip(string root)
    {
        string path = Path.Combine(root, "nested", "config.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "old content that must be replaced");

        Config source = new Config();
        source.Spawn.ChancePerEligibleEncounter = 0.42f;
        source.Attack.RunSpeedMetersPerSecond = 8.5f;
        source.Effects.ExplosionDamage = 123f;
        source.SinglePlayerOnly = false;
        source.Safety.AllowNetworkSends = true;

        Equal(true, source.Save(path), "Save result");
        Equal(true, File.Exists(path), "target exists");
        Equal(false, File.Exists(path + ".tmp"), "temporary file was moved away");

        Config loaded = Config.Load(path);
        Near(0.42f, loaded.Spawn.ChancePerEligibleEncounter, "round-trip chance");
        Near(8.5f, loaded.Attack.RunSpeedMetersPerSecond, "round-trip speed");
        Near(123f, loaded.Effects.ExplosionDamage, "round-trip damage");
        AssertSafePolicy(loaded);
    }

    private static void TestFullDamage()
    {
        Near(100f, ExplosionMath.CalculateDamage(0f, 2f, 5f, 100f), "at origin");
        Near(100f, ExplosionMath.CalculateDamage(1.5f, 2f, 5f, 100f), "inside trigger");
        Near(100f, ExplosionMath.CalculateDamage(2f, 2f, 5f, 100f), "at trigger boundary");
    }

    private static void TestLinearFalloff()
    {
        Near(50f, ExplosionMath.CalculateDamage(3.5f, 2f, 5f, 100f), "falloff midpoint");
        Near(25f, ExplosionMath.CalculateDamage(4.25f, 2f, 5f, 100f), "falloff final quarter");
    }

    private static void TestOutsideRadius()
    {
        Near(0f, ExplosionMath.CalculateDamage(5f, 2f, 5f, 100f), "at radius");
        Near(0f, ExplosionMath.CalculateDamage(50f, 2f, 5f, 100f), "outside radius");
    }

    private static void TestInvalidExplosionInputs()
    {
        Near(0f, ExplosionMath.CalculateDamage(float.NaN, 2f, 5f, 100f), "NaN distance");
        Near(0f, ExplosionMath.CalculateDamage(1f, float.NaN, 5f, 100f), "NaN trigger");
        Near(0f, ExplosionMath.CalculateDamage(1f, 2f, float.NaN, 100f), "NaN radius");
        Near(0f, ExplosionMath.CalculateDamage(1f, 2f, 5f, float.NaN), "NaN damage");
        Near(0f, ExplosionMath.CalculateDamage(float.PositiveInfinity, 2f, 5f, 100f), "infinite distance");
        Near(0f, ExplosionMath.CalculateDamage(-1f, 2f, 5f, 100f), "negative distance");
        Near(0f, ExplosionMath.CalculateDamage(1f, 2f, 0f, 100f), "zero radius");
        Near(0f, ExplosionMath.CalculateDamage(1f, 2f, 5f, -100f), "negative damage");
    }

    private static void AssertSafePolicy(Config config)
    {
        Equal(true, config.SinglePlayerOnly, "SinglePlayerOnly policy");
        Equal(true, config.Safety.DisableInMultiplayer, "DisableInMultiplayer policy");
        Equal(false, config.Safety.AllowNetworkSends, "AllowNetworkSends policy");
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine("PASS: " + name);
        }
        catch (Exception ex)
        {
            Failures.Add(name + ": " + ex.Message);
        }
    }

    private static void Near(float expected, float actual, string name)
    {
        if (float.IsNaN(actual) || Math.Abs(expected - actual) > 0.0001f)
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{name}: expected {expected}, got {actual}");
    }

    private static void NotNull(object value, string name)
    {
        if (value == null)
            throw new InvalidOperationException(name + " was null");
    }
}

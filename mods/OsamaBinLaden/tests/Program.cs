using System;
using System.Collections.Generic;
using System.IO;
using OsamaBinLaden;
using OsamaBinLaden.Multiplayer;

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
            Run("Encounter protocol key is stable and four-part", TestProtocolKey);
            Run("Encounter protocol round-trips every message type", TestProtocolRoundTrip);
            Run("Encounter protocol rejects every truncation", TestProtocolTruncation);
            Run("Encounter protocol rejects wrong magic and version", TestProtocolHeaderValidation);
            Run("Encounter protocol enforces extreme caps", TestProtocolCaps);
            Run("Encounter protocol marks authoritative messages", TestProtocolAuthorityMetadata);
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
            Console.WriteLine("PASS: all 14 smoke tests passed");
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

    private static void TestProtocolKey()
    {
        ReliableKey4 key = EncounterProtocol.ReliableKey;
        Equal(unchecked((int)0x4f424c4e), key.Part0, "reliable key part 0");
        Equal(unchecked((int)0x454e434e), key.Part1, "reliable key part 1");
        Equal(unchecked((int)0x50524f54), key.Part2, "reliable key part 2");
        Equal(unchecked((int)0x00010000), key.Part3, "reliable key part 3");

        Equal(true, key.Equals(new ReliableKey4(key.Part0, key.Part1, key.Part2, key.Part3)), "key equality");
        Equal(false, key.Equals(new ReliableKey4(key.Part0, key.Part1, key.Part2, key.Part3 + 1)), "key inequality");
    }

    private static void TestProtocolRoundTrip()
    {
        EncounterMessageType[] types =
        {
            EncounterMessageType.Hello,
            EncounterMessageType.HelloAck,
            EncounterMessageType.Spawn,
            EncounterMessageType.Detonate,
            EncounterMessageType.Cancel,
            EncounterMessageType.Heartbeat
        };

        foreach (EncounterMessageType type in types)
        {
            EncounterMessage source = CreateProtocolMessage(type);
            Equal(true, EncounterProtocol.TryEncode(source, out byte[] packet), type + " encode");
            Equal(EncounterProtocol.PacketSize, packet.Length, type + " packet size");

            // The fixed header itself is explicitly little-endian.
            Equal((byte)0x4f, packet[0], type + " magic byte 0");
            Equal((byte)0x42, packet[1], type + " magic byte 1");
            Equal((byte)0x4c, packet[2], type + " magic byte 2");
            Equal((byte)0x4e, packet[3], type + " magic byte 3");
            Equal((byte)1, packet[4], type + " version low byte");
            Equal((byte)0, packet[5], type + " version high byte");

            Equal(true, EncounterProtocol.TryDecode(packet, out EncounterMessage decoded), type + " decode");
            Equal(source.Type, decoded.Type, type + " type");
            Equal(source.Reason, decoded.Reason, type + " reason");
            Equal(source.Sequence, decoded.Sequence, type + " sequence");
            Equal(source.SessionNonce, decoded.SessionNonce, type + " nonce");
            Equal(source.EncounterId, decoded.EncounterId, type + " encounter id");
            Equal(source.HostPlayerId, decoded.HostPlayerId, type + " host id");
            Equal(source.TargetPlayerId, decoded.TargetPlayerId, type + " target id");
            Equal(source.HostTick, decoded.HostTick, type + " host tick");
            Near(source.SpawnX, decoded.SpawnX, type + " spawn x");
            Near(source.SpawnY, decoded.SpawnY, type + " spawn y");
            Near(source.SpawnZ, decoded.SpawnZ, type + " spawn z");
            Near(source.Config.RunSpeed, decoded.Config.RunSpeed, type + " speed");
            Near(source.Config.TriggerDistance, decoded.Config.TriggerDistance, type + " trigger");
            Near(source.Config.FuseSeconds, decoded.Config.FuseSeconds, type + " fuse");
            Near(source.Config.LifetimeSeconds, decoded.Config.LifetimeSeconds, type + " lifetime");
            Near(source.Config.VisualScale, decoded.Config.VisualScale, type + " scale");
            Near(source.Config.ScreamVolume, decoded.Config.ScreamVolume, type + " scream volume");
            Near(source.Config.ExplosionRadius, decoded.Config.ExplosionRadius, type + " radius");
            Near(source.Config.ExplosionDamage, decoded.Config.ExplosionDamage, type + " damage");

            byte[] framed = new byte[packet.Length + 4];
            Buffer.BlockCopy(packet, 0, framed, 2, packet.Length);
            Equal(true, EncounterProtocol.TryDecode(framed, 2, packet.Length, out _), type + " offset decode");
        }
    }

    private static void TestProtocolTruncation()
    {
        EncounterMessage source = CreateProtocolMessage(EncounterMessageType.Spawn);
        Equal(true, EncounterProtocol.TryEncode(source, out byte[] packet), "source encode");

        for (int length = 0; length < packet.Length; length++)
        {
            byte[] truncated = new byte[length];
            Buffer.BlockCopy(packet, 0, truncated, 0, length);
            Equal(false, EncounterProtocol.TryDecode(truncated, out _), "truncated length " + length);
        }

        byte[] trailing = new byte[packet.Length + 1];
        Buffer.BlockCopy(packet, 0, trailing, 0, packet.Length);
        Equal(false, EncounterProtocol.TryDecode(trailing, out _), "trailing byte");
        Equal(false, EncounterProtocol.TryDecode(packet, -1, packet.Length, out _), "negative offset");
        Equal(false, EncounterProtocol.TryDecode(packet, 1, packet.Length, out _), "range past end");
        Equal(false, EncounterProtocol.TryDecode(null, out _), "null packet");
    }

    private static void TestProtocolHeaderValidation()
    {
        Equal(true, EncounterProtocol.TryEncode(CreateProtocolMessage(EncounterMessageType.Spawn), out byte[] valid), "source encode");

        byte[] wrongMagic = (byte[])valid.Clone();
        wrongMagic[0] ^= 0xff;
        Equal(false, EncounterProtocol.TryDecode(wrongMagic, out _), "wrong magic");

        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[4] = 2;
        Equal(false, EncounterProtocol.TryDecode(wrongVersion, out _), "wrong version");

        byte[] unknownType = (byte[])valid.Clone();
        unknownType[6] = 255;
        Equal(false, EncounterProtocol.TryDecode(unknownType, out _), "unknown type");

        byte[] unknownReason = (byte[])valid.Clone();
        unknownReason[7] = 255;
        Equal(false, EncounterProtocol.TryDecode(unknownReason, out _), "unknown reason");

        byte[] wrongPayloadSize = (byte[])valid.Clone();
        wrongPayloadSize[8] = 0;
        Equal(false, EncounterProtocol.TryDecode(wrongPayloadSize, out _), "wrong payload size");

        byte[] nonZeroReserved = (byte[])valid.Clone();
        nonZeroReserved[10] = 1;
        Equal(false, EncounterProtocol.TryDecode(nonZeroReserved, out _), "non-zero reserved bits");
    }

    private static void TestProtocolCaps()
    {
        EncounterMessage boundary = CreateProtocolMessage(EncounterMessageType.Spawn);
        boundary.HostPlayerId = EncounterProtocol.MaximumPlayerId;
        boundary.TargetPlayerId = EncounterProtocol.MaximumPlayerId;
        boundary.SpawnX = -EncounterProtocol.MaximumCoordinateMagnitude;
        boundary.SpawnY = EncounterProtocol.MaximumCoordinateMagnitude;
        boundary.SpawnZ = 0f;
        boundary.Config = new EncounterConfigSnapshot
        {
            RunSpeed = 20f,
            TriggerDistance = 8f,
            FuseSeconds = 0f,
            LifetimeSeconds = 180f,
            VisualScale = 3f,
            ScreamVolume = 1f,
            ExplosionRadius = 20f,
            ExplosionDamage = 500f
        };
        Equal(true, EncounterProtocol.TryEncode(boundary, out byte[] boundaryPacket), "inclusive boundary encode");
        Equal(true, EncounterProtocol.TryDecode(boundaryPacket, out _), "inclusive boundary decode");

        EncounterMessage invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.SpawnX = EncounterProtocol.MaximumCoordinateMagnitude + 1f;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "coordinate cap");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.Sequence = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "zero sequence");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.SessionNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "zero nonce");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.EncounterId = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing encounter id");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.TargetPlayerId = -1;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing spawn target");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        EncounterConfigSnapshot invalidConfig = invalid.Config;
        invalidConfig.ExplosionDamage = 500.01f;
        invalid.Config = invalidConfig;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "damage cap");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalidConfig = invalid.Config;
        invalidConfig.RunSpeed = float.NaN;
        invalid.Config = invalidConfig;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "NaN config");

        // Speed starts at byte 64. Mutating a valid packet proves decode revalidates values,
        // instead of trusting packets merely because their fixed framing is intact.
        byte[] hostile = (byte[])boundaryPacket.Clone();
        byte[] infinity = BitConverter.GetBytes(float.PositiveInfinity);
        if (!BitConverter.IsLittleEndian) Array.Reverse(infinity);
        Buffer.BlockCopy(infinity, 0, hostile, 64, 4);
        Equal(false, EncounterProtocol.TryDecode(hostile, out _), "infinite decoded speed");
    }

    private static void TestProtocolAuthorityMetadata()
    {
        Equal(false, EncounterProtocol.RequiresHostSender(EncounterMessageType.Hello), "hello sender");
        Equal(false, EncounterProtocol.RequiresHostSender(EncounterMessageType.Heartbeat), "heartbeat sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Spawn), "spawn sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Detonate), "detonate sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Cancel), "cancel sender");
    }

    private static EncounterMessage CreateProtocolMessage(EncounterMessageType type)
    {
        return new EncounterMessage
        {
            Type = type,
            Reason = type == EncounterMessageType.Detonate
                ? EncounterReason.ReachedTarget
                : EncounterReason.HuntStarted,
            Sequence = 0x0102030405060708UL,
            SessionNonce = 0xf1e2d3c4b5a69788UL,
            EncounterId = type == EncounterMessageType.Hello ||
                          type == EncounterMessageType.HelloAck ||
                          type == EncounterMessageType.Heartbeat
                ? 0UL
                : 0x1122334455667788UL,
            HostPlayerId = 1,
            TargetPlayerId = 2,
            HostTick = 0x8877665544332211UL,
            SpawnX = -12.5f,
            SpawnY = 3.25f,
            SpawnZ = 99.75f,
            Config = new EncounterConfigSnapshot
            {
                RunSpeed = 7.5f,
                TriggerDistance = 2.25f,
                FuseSeconds = 0.75f,
                LifetimeSeconds = 42f,
                VisualScale = 1.25f,
                ScreamVolume = 0.65f,
                ExplosionRadius = 5.5f,
                ExplosionDamage = 125f
            }
        };
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

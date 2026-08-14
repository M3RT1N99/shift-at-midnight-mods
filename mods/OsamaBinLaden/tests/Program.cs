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
            Run("Config clamps values and bounds multiplayer timing", TestClampAndPolicy);
            Run("Config mirrors the single-player-only opt-out both ways", TestSinglePlayerOnlyMirroring);
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
            Run("Encounter protocol enforces three-way handshake shapes", TestProtocolHandshakeValidation);
            Run("Encounter protocol marks authoritative messages", TestProtocolAuthorityMetadata);
            Run("SequenceGuard accepts strictly increasing sequences", TestSequenceGuardAccepts);
            Run("SequenceGuard rejects replays, duplicates and zero", TestSequenceGuardRejects);
            Run("SequenceGuard follows a newer epoch and refuses an older one", TestSequenceGuardEpoch);
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
            Console.WriteLine("PASS: all 19 smoke tests passed");
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
        AssertMirroredMultiplayerPolicy(missing);
        Equal(true, missing.Enabled, "missing config: Enabled");
        Equal(false, missing.SinglePlayerOnly, "missing config: multiplayer on by default");
        Equal(true, missing.Safety.AllowNetworkSends, "missing config: network sends allowed by default");
        Equal(1, missing.Spawn.MaximumActive, "missing config: MaximumActive");
        Near(6f, missing.Attack.RunSpeedMetersPerSecond, "missing config: run speed");
        Near(8f, missing.Multiplayer.HandshakeTimeoutSeconds, "missing config: handshake timeout");

        string malformedPath = Path.Combine(root, "malformed.json");
        File.WriteAllText(malformedPath, "{ this is not valid JSON");
        Config malformed = Config.Load(malformedPath);
        AssertMirroredMultiplayerPolicy(malformed);
        Equal(true, malformed.Spawn.Enabled, "malformed config: spawn enabled");
        Near(100f, malformed.Effects.ExplosionDamage, "malformed config: damage");
    }

    private static void TestNullSections(string root)
    {
        string path = Path.Combine(root, "null-sections.json");
        File.WriteAllText(
            path,
            "{\"singlePlayerOnly\":false,\"spawn\":null,\"attack\":null," +
            "\"effects\":null,\"safety\":null,\"logging\":null,\"multiplayer\":null}");

        Config config = Config.Load(path);

        NotNull(config.Spawn, "Spawn section");
        NotNull(config.Attack, "Attack section");
        NotNull(config.Effects, "Effects section");
        NotNull(config.Safety, "Safety section");
        NotNull(config.Logging, "Logging section");
        NotNull(config.Multiplayer, "Multiplayer section");
        AssertMirroredMultiplayerPolicy(config);
        Near(35f, config.Spawn.MaximumLifetimeSeconds, "repaired spawn defaults");
        Near(4f, config.Effects.ExplosionRadiusMeters, "repaired effects defaults");
        Near(4f, config.Multiplayer.HeartbeatIntervalSeconds, "repaired multiplayer defaults");
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
            Logging = new Config.LoggingCfg { Level = "trace" },
            Multiplayer = new Config.MultiplayerCfg
            {
                HandshakeTimeoutSeconds = -5f,
                HeartbeatIntervalSeconds = 999f,
                PeerTimeoutSeconds = 0f,
                HostMarkerRepublishSeconds = 999f
            }
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
        Equal(false, config.SinglePlayerOnly, "SinglePlayerOnly stays false when never requested");
        Equal(true, config.Safety.AllowNetworkSends, "AllowNetworkSends is left exactly as set");
        AssertMirroredMultiplayerPolicy(config);
        Near(2f, config.Multiplayer.HandshakeTimeoutSeconds, "handshake timeout lower bound");
        Near(15f, config.Multiplayer.HeartbeatIntervalSeconds, "heartbeat interval upper bound");
        Near(5f, config.Multiplayer.PeerTimeoutSeconds, "peer timeout lower bound");
        Near(30f, config.Multiplayer.HostMarkerRepublishSeconds, "marker republish upper bound");
    }

    private static void TestSinglePlayerOnlyMirroring()
    {
        Config bySinglePlayerOnly = new Config { SinglePlayerOnly = true };
        bySinglePlayerOnly.Clamp();
        Equal(true, bySinglePlayerOnly.SinglePlayerOnly, "SinglePlayerOnly stays true when requested");
        Equal(true, bySinglePlayerOnly.Safety.DisableInMultiplayer, "SinglePlayerOnly forces DisableInMultiplayer true");

        Config byDisableInMultiplayer = new Config();
        byDisableInMultiplayer.Safety.DisableInMultiplayer = true;
        byDisableInMultiplayer.Clamp();
        Equal(true, byDisableInMultiplayer.SinglePlayerOnly, "DisableInMultiplayer forces SinglePlayerOnly true");
        Equal(true, byDisableInMultiplayer.Safety.DisableInMultiplayer, "DisableInMultiplayer stays true");

        Config neither = new Config();
        neither.Clamp();
        Equal(false, neither.SinglePlayerOnly, "multiplayer stays on when neither opt-out is set");
        Equal(false, neither.Safety.DisableInMultiplayer, "multiplayer stays on when neither opt-out is set");
        Equal(true, neither.Safety.AllowNetworkSends, "network sends are allowed by default");
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
        Equal(false, loaded.SinglePlayerOnly, "round-trip SinglePlayerOnly stays false");
        Equal(true, loaded.Safety.AllowNetworkSends, "round-trip AllowNetworkSends stays true");
        AssertMirroredMultiplayerPolicy(loaded);
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
            EncounterMessageType.Ready,
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
            Equal(source.HostEpoch, decoded.HostEpoch, type + " host epoch");
            Equal(source.ClientNonce, decoded.ClientNonce, type + " client nonce");
            Equal(source.HostNonce, decoded.HostNonce, type + " host nonce");
            Equal(source.EncounterId, decoded.EncounterId, type + " encounter id");
            Equal(source.HostPlayerId, decoded.HostPlayerId, type + " host id");
            Equal(source.TargetPlayerId, decoded.TargetPlayerId, type + " target id");
            Equal(source.TargetPlayerRawEncoded, decoded.TargetPlayerRawEncoded, type + " target raw id");
            Equal(source.TargetNetworkId, decoded.TargetNetworkId, type + " target network id");
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
        boundary.TargetPlayerRawEncoded = EncounterProtocol.MaximumPlayerRawEncoded;
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
        invalid.ClientNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "zero client nonce");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.HostNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "zero host nonce");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.HostEpoch = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "zero host epoch");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.EncounterId = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing encounter id");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.TargetPlayerId = -1;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing spawn target");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.TargetPlayerRawEncoded = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing target raw id");

        invalid = CreateProtocolMessage(EncounterMessageType.Spawn);
        invalid.TargetNetworkId = 0;
        Equal(false, EncounterProtocol.TryEncode(invalid, out _), "missing target network id");

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

        // Speed starts at byte 88. Mutating a valid packet proves decode revalidates values,
        // instead of trusting packets merely because their fixed framing is intact.
        byte[] hostile = (byte[])boundaryPacket.Clone();
        byte[] infinity = BitConverter.GetBytes(float.PositiveInfinity);
        if (!BitConverter.IsLittleEndian) Array.Reverse(infinity);
        Buffer.BlockCopy(infinity, 0, hostile, 88, 4);
        Equal(false, EncounterProtocol.TryDecode(hostile, out _), "infinite decoded speed");
    }

    private static void TestProtocolHandshakeValidation()
    {
        EncounterMessage hello = CreateProtocolMessage(EncounterMessageType.Hello);
        Equal(true, EncounterProtocol.TryEncode(hello, out _), "valid hello");

        hello.ClientNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(hello, out _), "hello needs client challenge");

        hello = CreateProtocolMessage(EncounterMessageType.Hello);
        hello.HostEpoch = 1;
        Equal(false, EncounterProtocol.TryEncode(hello, out _), "hello cannot claim host epoch");

        hello = CreateProtocolMessage(EncounterMessageType.Hello);
        hello.HostNonce = 1;
        Equal(false, EncounterProtocol.TryEncode(hello, out _), "hello cannot claim host challenge");

        EncounterMessage ack = CreateProtocolMessage(EncounterMessageType.HelloAck);
        Equal(true, EncounterProtocol.TryEncode(ack, out _), "valid hello ack");
        ack.ClientNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(ack, out _), "ack needs echoed client challenge");

        ack = CreateProtocolMessage(EncounterMessageType.HelloAck);
        ack.HostNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(ack, out _), "ack needs host challenge");

        ack = CreateProtocolMessage(EncounterMessageType.HelloAck);
        ack.HostEpoch = 0;
        Equal(false, EncounterProtocol.TryEncode(ack, out _), "ack needs host epoch");

        EncounterMessage ready = CreateProtocolMessage(EncounterMessageType.Ready);
        Equal(true, EncounterProtocol.TryEncode(ready, out _), "valid ready");
        ready.HostNonce = 0;
        Equal(false, EncounterProtocol.TryEncode(ready, out _), "ready echoes host challenge");

        ready = CreateProtocolMessage(EncounterMessageType.Ready);
        ready.EncounterId = 1;
        Equal(false, EncounterProtocol.TryEncode(ready, out _), "ready is not an encounter");

        EncounterMessage heartbeat = CreateProtocolMessage(EncounterMessageType.Heartbeat);
        Equal(true, EncounterProtocol.TryEncode(heartbeat, out _), "valid heartbeat");
        heartbeat.TargetNetworkId = 1;
        Equal(false, EncounterProtocol.TryEncode(heartbeat, out _), "heartbeat has no target object");
    }

    private static void TestProtocolAuthorityMetadata()
    {
        Equal(false, EncounterProtocol.RequiresHostSender(EncounterMessageType.Hello), "hello sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.HelloAck), "hello ack sender");
        Equal(false, EncounterProtocol.RequiresHostSender(EncounterMessageType.Ready), "ready sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Heartbeat), "heartbeat sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Spawn), "spawn sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Detonate), "detonate sender");
        Equal(true, EncounterProtocol.RequiresHostSender(EncounterMessageType.Cancel), "cancel sender");
    }

    private static void TestSequenceGuardAccepts()
    {
        var guard = new SequenceGuard();
        Equal(true, guard.TryAccept(1, 1), "first sequence in epoch 1");
        Equal(true, guard.TryAccept(1, 2), "second sequence in epoch 1");
        Equal(true, guard.TryAccept(1, 10), "sequence may skip ahead");
    }

    private static void TestSequenceGuardRejects()
    {
        var guard = new SequenceGuard();
        Equal(false, guard.TryAccept(1, 0), "sequence zero is never valid");
        Equal(true, guard.TryAccept(1, 5), "establish a baseline");
        Equal(false, guard.TryAccept(1, 5), "exact replay is rejected");
        Equal(false, guard.TryAccept(1, 3), "older sequence is rejected");
        Equal(false, guard.TryAccept(0, 6), "an older epoch is rejected outright");

        guard.Reset();
        Equal(true, guard.TryAccept(1, 1), "reset clears the tracked baseline");
    }

    private static void TestSequenceGuardEpoch()
    {
        var guard = new SequenceGuard();
        Equal(true, guard.TryAccept(1, 100), "baseline in epoch 1");
        Equal(true, guard.TryAccept(2, 1), "a newer epoch resets the sequence floor");
        Equal(false, guard.TryAccept(2, 1), "the same (epoch, sequence) pair cannot repeat");
        Equal(true, guard.TryAccept(2, 2), "epoch 2 still requires increasing sequences");
        Equal(false, guard.TryAccept(1, 999), "epoch 1 can never come back once epoch 2 was seen");
    }

    private static EncounterMessage CreateProtocolMessage(EncounterMessageType type)
    {
        bool encounter = type == EncounterMessageType.Spawn ||
                         type == EncounterMessageType.Detonate ||
                         type == EncounterMessageType.Cancel;
        bool hello = type == EncounterMessageType.Hello;

        return new EncounterMessage
        {
            Type = type,
            Reason = !encounter
                ? EncounterReason.None
                : type == EncounterMessageType.Detonate
                    ? EncounterReason.ReachedTarget
                    : EncounterReason.HuntStarted,
            Sequence = 0x0102030405060708UL,
            HostEpoch = hello ? 0UL : 0x1111222233334444UL,
            ClientNonce = 0xf1e2d3c4b5a69788UL,
            HostNonce = hello ? 0UL : 0x1020304050607080UL,
            EncounterId = encounter ? 0x1122334455667788UL : 0UL,
            HostPlayerId = hello ? -1 : 1,
            TargetPlayerId = encounter ? 2 : -1,
            TargetPlayerRawEncoded = encounter ? 3 : 0,
            TargetNetworkId = encounter ? 0xaabbccddU : 0U,
            HostTick = hello ? 0UL : 0x8877665544332211UL,
            SpawnX = encounter ? -12.5f : 0f,
            SpawnY = encounter ? 3.25f : 0f,
            SpawnZ = encounter ? 99.75f : 0f,
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

    /// <summary>SinglePlayerOnly and Safety.DisableInMultiplayer are two names for the same
    /// opt-out and must never disagree after Clamp, whichever value each started with.</summary>
    private static void AssertMirroredMultiplayerPolicy(Config config)
    {
        Equal(config.SinglePlayerOnly, config.Safety.DisableInMultiplayer,
            "SinglePlayerOnly mirrors Safety.DisableInMultiplayer");
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

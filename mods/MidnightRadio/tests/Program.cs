using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidnightRadio.Sync;

namespace MidnightRadio.SmokeTests
{
    internal static class Program
    {
        private static readonly List<string> CapturedLogs = new List<string>();
        private static int _passed;
        private static int _failed;

        private static int Main()
        {
            Log.Bind(
                message => CapturedLogs.Add("INFO " + message),
                message => CapturedLogs.Add("WARN " + message),
                message => CapturedLogs.Add("ERROR " + message));

            Run("protocol primitive roundtrip", ProtocolPrimitiveRoundtrip);
            Run("protocol factory roundtrips", ProtocolFactoryRoundtrips);
            Run("protocol offset segment", ProtocolOffsetSegment);
            Run("protocol malformed headers", ProtocolMalformedHeaders);
            Run("protocol truncated payloads", ProtocolTruncatedPayloads);
            Run("protocol string byte caps", ProtocolStringByteCaps);

            Run("config clamps unsafe values", ConfigClampsUnsafeValues);
            Run("config save/load roundtrip", ConfigSaveLoadRoundtrip);
            Run("config fills missing sections", ConfigFillsMissingSections);
            Run("config missing/malformed fallback", ConfigFallbacks);
            Run("config migrates legacy sync settings", ConfigMigratesLegacySync);

            Run("music library scans and deduplicates", MusicLibraryScansAndDeduplicates);
            Run("music library rebuilds malformed cache", MusicLibraryRebuildsMalformedCache);
            Run("external tool consent and URL gates", ExternalToolConsentAndUrlGates);

            Console.WriteLine();
            Console.WriteLine($"Smoke tests: {_passed} passed, {_failed} failed");
            if (CapturedLogs.Count != 0)
                Console.WriteLine($"Captured {CapturedLogs.Count} expected production log message(s).");

            return _failed == 0 ? 0 : 1;
        }

        private static void Run(string name, Action test)
        {
            try
            {
                test();
                _passed++;
                Console.WriteLine("[PASS] " + name);
            }
            catch (Exception ex)
            {
                _failed++;
                Console.WriteLine("[FAIL] " + name);
                Console.WriteLine("       " + ex.Message);
            }
        }

        private static void ProtocolPrimitiveRoundtrip()
        {
            var frame = new SyncProtocol.Writer(SyncProtocol.MsgType.QueueAdd, 0)
                .U8(byte.MaxValue)
                .Bool(true)
                .Bool(false)
                .U16(0xBEEF)
                .I32(int.MinValue)
                .I32(int.MaxValue)
                .F32(-123.5f)
                .Str("Grüße 🎵")
                .Str(null)
                .Str(string.Empty)
                .Done();

            var reader = Open(frame, SyncProtocol.MsgType.QueueAdd);
            Equal(byte.MaxValue, reader.U8(), "U8");
            True(reader.Bool(), "true bool");
            False(reader.Bool(), "false bool");
            Equal((ushort)0xBEEF, reader.U16(), "U16");
            Equal(int.MinValue, reader.I32(), "negative I32");
            Equal(int.MaxValue, reader.I32(), "positive I32");
            Near(-123.5f, reader.F32(), "F32");
            Equal("Grüße 🎵", reader.Str(), "UTF-8 string");
            Equal(string.Empty, reader.Str(), "null string");
            Equal(string.Empty, reader.Str(), "empty string");
            True(reader.Ok, "reader stays valid after complete payload");

            Equal((byte)0, reader.U8(), "past-end read fallback");
            False(reader.Ok, "past-end read invalidates reader");
        }

        private static void ProtocolFactoryRoundtrips()
        {
            var hello = Open(
                SyncProtocol.Hello("Nachtwächter 🎵", true),
                SyncProtocol.MsgType.Hello);
            Equal("Nachtwächter 🎵", hello.Str(), "hello display name");
            True(hello.Bool(), "hello URL capability");
            True(hello.Ok, "hello payload");

            var nowPlaying = Open(
                SyncProtocol.NowPlaying(
                    SyncProtocol.TrackSource.Url,
                    "https://example.invalid/song?id=42",
                    "Mitternacht",
                    -123456,
                    12.25f,
                    245.75f,
                    int.MaxValue),
                SyncProtocol.MsgType.NowPlaying);
            Equal((byte)SyncProtocol.TrackSource.Url, nowPlaying.U8(), "track source");
            Equal("https://example.invalid/song?id=42", nowPlaying.Str(), "track id");
            Equal("Mitternacht", nowPlaying.Str(), "track title");
            Equal(-123456, nowPlaying.I32(), "start tick");
            Near(12.25f, nowPlaying.F32(), "start offset");
            Near(245.75f, nowPlaying.F32(), "duration");
            Equal(int.MaxValue, nowPlaying.I32(), "state revision");
            True(nowPlaying.Ok, "now-playing payload");

            var control = Open(
                SyncProtocol.Control(SyncProtocol.ControlOp.Seek, 998877, 91.5f, -7),
                SyncProtocol.MsgType.Control);
            Equal((byte)SyncProtocol.ControlOp.Seek, control.U8(), "control op");
            Equal(998877, control.I32(), "control tick");
            Near(91.5f, control.F32(), "control position");
            Equal(-7, control.I32(), "control revision");
            True(control.Ok, "control payload");

            var need = Open(
                SyncProtocol.Need("ABCDEF", SyncProtocol.NeedReason.DecodeFailed),
                SyncProtocol.MsgType.Need);
            Equal("ABCDEF", need.Str(), "need track id");
            Equal((byte)SyncProtocol.NeedReason.DecodeFailed, need.U8(), "need reason");
            True(need.Ok, "need payload");

            var have = Open(SyncProtocol.Have("0123456789abcdef"), SyncProtocol.MsgType.Have);
            Equal("0123456789abcdef", have.Str(), "have track id");
            True(have.Ok, "have payload");
        }

        private static void ProtocolOffsetSegment()
        {
            var original = SyncProtocol.Hello("offset", false);
            var wrapped = new byte[original.Count + 11];
            Buffer.BlockCopy(original.Array, original.Offset, wrapped, 7, original.Count);

            var reader = Open(
                new ArraySegment<byte>(wrapped, 7, original.Count),
                SyncProtocol.MsgType.Hello);
            Equal("offset", reader.Str(), "offset string");
            False(reader.Bool(), "offset bool");
            True(reader.Ok, "offset payload");
        }

        private static void ProtocolMalformedHeaders()
        {
            False(new SyncProtocol.Reader(default).Ok, "default segment");

            var valid = SyncProtocol.Hello("x", true);
            for (int length = 0; length < 4; length++)
            {
                var truncated = new ArraySegment<byte>(valid.Array, valid.Offset, length);
                False(new SyncProtocol.Reader(truncated).Ok, "header length " + length);
            }

            var badMagic = CopyFrame(valid);
            badMagic[0] ^= 0xFF;
            False(new SyncProtocol.Reader(new ArraySegment<byte>(badMagic)).Ok, "bad magic");

            var shortString = new byte[]
            {
                (byte)(SyncProtocol.Magic & 0xFF),
                (byte)(SyncProtocol.Magic >> 8),
                SyncProtocol.Version,
                (byte)SyncProtocol.MsgType.Have,
                5, 0,
                (byte)'a', (byte)'b',
            };
            var reader = Open(new ArraySegment<byte>(shortString), SyncProtocol.MsgType.Have);
            Equal(string.Empty, reader.Str(), "truncated string fallback");
            False(reader.Ok, "truncated string invalidates reader");
        }

        private static void ProtocolTruncatedPayloads()
        {
            var complete = SyncProtocol.NowPlaying(
                SyncProtocol.TrackSource.LocalFile,
                new string('a', 80),
                "Titel 🎶",
                123,
                4.5f,
                99.25f,
                8);

            for (int length = 0; length < complete.Count; length++)
            {
                var segment = new ArraySegment<byte>(complete.Array, complete.Offset, length);
                var reader = new SyncProtocol.Reader(segment);
                if (reader.Ok)
                {
                    reader.U8();
                    reader.Str();
                    reader.Str();
                    reader.I32();
                    reader.F32();
                    reader.F32();
                    reader.I32();
                }

                False(reader.Ok, "truncated frame length " + length);
            }

            var fullReader = Open(complete, SyncProtocol.MsgType.NowPlaying);
            fullReader.U8();
            fullReader.Str();
            fullReader.Str();
            fullReader.I32();
            fullReader.F32();
            fullReader.F32();
            fullReader.I32();
            True(fullReader.Ok, "complete control frame remains valid");
        }

        private static void ProtocolStringByteCaps()
        {
            var ascii = Open(
                new SyncProtocol.Writer(SyncProtocol.MsgType.QueueAdd)
                    .Str(new string('x', 100), 64)
                    .Done(),
                SyncProtocol.MsgType.QueueAdd);
            Equal(new string('x', 64), ascii.Str(), "ASCII cap");
            True(ascii.Ok, "ASCII cap payload");

            // A byte cap must stop before a UTF-8 code point rather than put a replacement
            // character on the wire. Five bytes can hold one four-byte emoji, not two.
            var unicodeFrame = new SyncProtocol.Writer(SyncProtocol.MsgType.QueueAdd)
                .Str("😀😀", 5)
                .Done();
            Equal(10, unicodeFrame.Count, "UTF-8 frame size");
            var unicode = Open(unicodeFrame, SyncProtocol.MsgType.QueueAdd);
            Equal("😀", unicode.Str(), "UTF-8 boundary cap");
            True(unicode.Ok, "UTF-8 cap payload");

            // The length prefix is U16, so even an accidentally larger caller-provided cap
            // must not wrap and leave extra bytes looking like the next protocol field.
            var oversized = Open(
                new SyncProtocol.Writer(SyncProtocol.MsgType.QueueAdd)
                    .Str(new string('z', ushort.MaxValue + 100), int.MaxValue)
                    .Done(),
                SyncProtocol.MsgType.QueueAdd);
            Equal(new string('z', ushort.MaxValue), oversized.Str(), "U16 string cap");
            True(oversized.Ok, "U16 cap payload");
        }

        private static void ConfigClampsUnsafeValues()
        {
            var config = new Config();
            config.Playback.VolumeCeiling = 2f;
            config.Playback.Volume = -1f;
            config.Playback.MaxRangeMeters = 0f;
            config.Playback.MaxTrackMinutes = 2000;
            config.Sync.SoftCorrectAboveMs = 1f;
            config.Sync.HardSeekAboveMs = 25f;
            config.Sync.SoftCorrectPitchRange = 0f;
            config.Sync.ResyncIntervalSeconds = 0f;
            config.Sync.StartLeadMs = 0;
            config.Sync.PrefetchLeadSeconds = 999;
            config.Sync.PeerToPeerTransfer.MaxFileMB = 0;
            config.Sync.PeerToPeerTransfer.MaxRateKBps = 20000;
            config.UrlMode.MaxDurationMinutes = 0;
            config.UrlMode.ResolveTimeoutSeconds = 999;
            config.Cache.MaxCacheMB = 0;
            config.Logging.KeepDays = 999;

            config.Clamp();

            Near(1f, config.Playback.VolumeCeiling, "volume ceiling");
            Near(0f, config.Playback.Volume, "volume floor");
            Near(1f, config.Playback.MaxRangeMeters, "range floor");
            Equal(1440, config.Playback.MaxTrackMinutes, "track duration cap");
            Near(20f, config.Sync.SoftCorrectAboveMs, "soft-correct floor");
            Near(70f, config.Sync.HardSeekAboveMs, "hard seek relation");
            Near(0.001f, config.Sync.SoftCorrectPitchRange, "pitch floor");
            Near(1f, config.Sync.ResyncIntervalSeconds, "resync floor");
            Equal(250, config.Sync.StartLeadMs, "start lead floor");
            Equal(120, config.Sync.PrefetchLeadSeconds, "prefetch cap");
            Equal(1, config.Sync.PeerToPeerTransfer.MaxFileMB, "transfer size floor");
            Equal(10240, config.Sync.PeerToPeerTransfer.MaxRateKBps, "transfer rate cap");
            Equal(1, config.UrlMode.MaxDurationMinutes, "URL duration floor");
            Equal(900, config.UrlMode.ResolveTimeoutSeconds, "URL timeout cap");
            Equal(64, config.Cache.MaxCacheMB, "cache floor");
            Equal(365, config.Logging.KeepDays, "log retention cap");

            config.Playback.VolumeCeiling = 0.25f;
            config.Playback.Volume = 0.9f;
            config.Clamp();
            Near(0.25f, config.Playback.Volume, "volume follows ceiling");
        }

        private static void ConfigSaveLoadRoundtrip()
        {
            InTempDirectory(root =>
            {
                string path = Path.Combine(root, "nested", "config.json");
                var config = new Config
                {
                    Version = 9,
                    Hotkey = "F8",
                    Language = "de",
                    MusicDirs = new List<string> { "Musik", "D:\\Audio 🎵" },
                };
                config.Playback.VolumeCeiling = 0.6f;
                config.Playback.Volume = 0.8f;
                config.Playback.Shuffle = true;
                config.Sync.AnyoneCanSkip = false;
                config.Sync.BlockedPlayers.Add("Spieler Eins");
                config.UrlMode.AudioFormat = "mp3";
                config.Tools.YtDlpPath = "Tools/yt-dlp.exe";
                config.Cache.MaxCacheMB = 4096;
                config.Logging.SyncDiagnostics = true;

                True(config.Save(path), "save succeeds");
                True(File.Exists(path), "config file exists");
                False(File.Exists(path + ".tmp"), "temporary file was replaced");

                byte[] bytes = File.ReadAllBytes(path);
                False(
                    bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                    "saved JSON has no UTF-8 BOM");

                var loaded = Config.Load(path);
                Equal(9, loaded.Version, "version");
                Equal("F8", loaded.Hotkey, "hotkey");
                Equal("de", loaded.Language, "language");
                SequenceEqual(config.MusicDirs, loaded.MusicDirs, "music directories");
                Near(0.6f, loaded.Playback.VolumeCeiling, "saved ceiling");
                Near(0.6f, loaded.Playback.Volume, "save-time clamp");
                True(loaded.Playback.Shuffle, "shuffle");
                False(loaded.Sync.AnyoneCanSkip, "skip permission");
                SequenceEqual(new[] { "Spieler Eins" }, loaded.Sync.BlockedPlayers, "blocked players");
                Equal("mp3", loaded.UrlMode.AudioFormat, "audio format");
                Equal("Tools/yt-dlp.exe", loaded.Tools.YtDlpPath, "tool path");
                Equal(4096, loaded.Cache.MaxCacheMB, "cache size");
                True(loaded.Logging.SyncDiagnostics, "sync diagnostics");
            });
        }

        private static void ConfigFillsMissingSections()
        {
            InTempDirectory(root =>
            {
                string path = Path.Combine(root, "config.json");
                File.WriteAllText(path, @"
                {
                  // Deliberately null or absent sections model an older hand-edited file.
                  ""version"": 3,
                  ""hotkey"": ""F7"",
                  ""musicDirs"": null,
                  ""playback"": null,
                  ""sync"": {
                    ""startLeadMs"": 1,
                    ""blockedPlayers"": null,
                    ""peerToPeerTransfer"": null,
                  },
                  ""urlMode"": null,
                  ""tools"": null,
                  ""cache"": null,
                  ""logging"": null,
                }");

                var config = Config.Load(path);
                // Version 3 is now migrated forward rather than preserved; this case is
                // about the null sections below being filled in, not about the number.
                Equal(Config.CurrentVersion, config.Version, "version brought current");
                Equal("F7", config.Hotkey, "preserved hotkey");
                NotNull(config.MusicDirs, "music directories");
                NotNull(config.Playback, "playback section");
                NotNull(config.Sync, "sync section");
                NotNull(config.Sync.BlockedPlayers, "blocked players");
                NotNull(config.Sync.PeerToPeerTransfer, "peer transfer section");
                NotNull(config.UrlMode, "URL section");
                NotNull(config.Tools, "tools section");
                NotNull(config.Cache, "cache section");
                NotNull(config.Logging, "logging section");
                Equal(250, config.Sync.StartLeadMs, "loaded value was clamped");
                Near(0.15f, config.Playback.Volume, "missing-section default");
            });
        }

        private static void ConfigFallbacks()
        {
            InTempDirectory(root =>
            {
                CapturedLogs.Clear();
                string missing = Path.Combine(root, "missing.json");
                var absent = Config.Load(missing);
                Equal(Config.CurrentVersion, absent.Version, "missing-file defaults");
                True(
                    CapturedLogs.Any(message => message.Contains("no config", StringComparison.Ordinal)),
                    "missing-file log");

                CapturedLogs.Clear();
                string malformed = Path.Combine(root, "broken.json");
                File.WriteAllText(malformed, "{ this is not JSON");
                var broken = Config.Load(malformed);
                Equal(Config.CurrentVersion, broken.Version, "malformed-file defaults");
                True(
                    CapturedLogs.Any(message => message.Contains("config load failed", StringComparison.Ordinal)),
                    "malformed-file warning");
            });
        }

        /// <summary>
        /// A config written by an earlier build carries synced playback switched off and,
        /// in at least one shipped case, a zero volume. Defaults never reach an existing
        /// file, so loading has to repair it.
        /// </summary>
        private static void ConfigMigratesLegacySync()
        {
            InTempDirectory(root =>
            {
                CapturedLogs.Clear();
                string path = Path.Combine(root, "legacy.json");
                File.WriteAllText(path, """
                {
                  "version": 2,
                  "playback": { "volume": 0 },
                  "sync": {
                    "enabled": false,
                    "acceptFromOthers": false,
                    "anyoneCanQueue": false,
                    "anyoneCanSkip": false
                  }
                }
                """);

                var migrated = Config.Load(path);

                Equal(Config.CurrentVersion, migrated.Version, "version bumped");
                True(migrated.Sync.Enabled, "sync re-enabled");
                True(migrated.Sync.AcceptFromOthers, "accepts others again");
                True(migrated.Sync.AnyoneCanQueue, "open queue restored");
                True(migrated.Sync.AnyoneCanSkip, "skip restored");
                Near(0.15f, migrated.Playback.Volume, "silent volume repaired");
                True(
                    CapturedLogs.Any(m => m.Contains("config migrated", StringComparison.Ordinal)),
                    "migration logged");

                // A current file must pass through untouched - migration is not a reset.
                CapturedLogs.Clear();
                string current = Path.Combine(root, "current.json");
                File.WriteAllText(current,
                    $$"""{ "version": {{Config.CurrentVersion}}, "sync": { "enabled": false } }""");

                var kept = Config.Load(current);
                False(kept.Sync.Enabled, "deliberate opt-out preserved");
            });
        }

        private static void MusicLibraryScansAndDeduplicates()
        {
            InTempDirectory(root =>
            {
                byte[] duplicateContent = Encoding.UTF8.GetBytes("same pretend audio bytes");
                byte[] uniqueContent = Encoding.UTF8.GetBytes("different pretend audio bytes");

                string defaultMusic = Path.Combine(root, "Music");
                string configuredMusic = Path.Combine(root, "Extra");
                string downloadCache = Path.Combine(root, "Cache", "_ytdlp");
                Directory.CreateDirectory(Path.Combine(defaultMusic, "Nested"));
                Directory.CreateDirectory(configuredMusic);
                Directory.CreateDirectory(downloadCache);
                File.WriteAllBytes(Path.Combine(defaultMusic, "Night.MP3"), duplicateContent);
                File.WriteAllBytes(Path.Combine(configuredMusic, "Copy.ogg"), duplicateContent);
                File.WriteAllBytes(Path.Combine(configuredMusic, "Other.wav"), uniqueContent);
                File.WriteAllBytes(Path.Combine(downloadCache, "Fetched.ogg"), uniqueContent);
                File.WriteAllText(Path.Combine(defaultMusic, "Nested", "ignored.txt"), "not audio");

                var config = new Config
                {
                    MusicDirs = new List<string> { "Extra", "Extra" },
                };
                var library = new MusicLibrary(root, config);
                var snapshot = library.Reload();

                Equal(2, library.Count, "distinct content count");
                Equal(2, snapshot.Count, "snapshot count");
                True(File.Exists(library.IndexPath), "library cache exists");
                True(MusicLibrary.IsSupportedPath("song.OgG"), "case-insensitive extension");
                False(MusicLibrary.IsSupportedPath("song.flac"), "unsupported extension");
                False(MusicLibrary.IsSupportedPath(null), "null path");

                string duplicateHash = Sha256(duplicateContent);
                True(library.TryGetTrack(duplicateHash.ToUpperInvariant(), out var duplicate), "hash lookup");
                Equal(duplicateHash, duplicate.Id, "normalized content id");
                Equal(2, duplicate.Paths.Count, "duplicate paths retained");
                Equal("Night", duplicate.Title, "deterministic first title");
                True(library.TryGetPath(duplicateHash, out var preferredPath), "preferred path lookup");
                Equal(Path.GetFullPath(Path.Combine(defaultMusic, "Night.MP3")), preferredPath, "preferred path");
                False(library.TryGetTrack("not-a-hash", out _), "unknown hash");

                var mutableView = (IList<TrackInfo>)snapshot;
                Throws<NotSupportedException>(() => mutableView.Add(duplicate), "snapshot is immutable");

                using var index = JsonDocument.Parse(File.ReadAllText(library.IndexPath));
                Equal(4, index.RootElement.GetProperty("files").GetArrayLength(), "one cache entry per file");
            });
        }

        private static void MusicLibraryRebuildsMalformedCache()
        {
            InTempDirectory(root =>
            {
                string music = Path.Combine(root, "Music");
                Directory.CreateDirectory(music);
                byte[] content = { 0, 1, 2, 3, 4, 5 };
                File.WriteAllBytes(Path.Combine(music, "track.aiff"), content);
                File.WriteAllText(Path.Combine(root, "library.json"), "not json");

                var library = new MusicLibrary(root, new Config());
                var snapshot = library.Reload();

                Equal(1, snapshot.Count, "scan survived bad cache");
                Equal(Sha256(content), snapshot[0].Id, "rebuilt hash");
                using var rebuilt = JsonDocument.Parse(File.ReadAllText(library.IndexPath));
                Equal(1, rebuilt.RootElement.GetProperty("version").GetInt32(), "cache version");
                Equal(1, rebuilt.RootElement.GetProperty("files").GetArrayLength(), "rebuilt cache entry");
            });
        }

        private static void ExternalToolConsentAndUrlGates()
        {
            InTempDirectory(root =>
            {
                var config = new Config();
                var bridge = new YtDlpBridge(config, root);

                DownloadResult disabled = bridge.DownloadAsync("https://example.com/watch?v=one")
                    .GetAwaiter().GetResult();
                Equal(DownloadErrorCode.UrlModeDisabled, disabled.ErrorCode, "disabled gate");

                config.UrlMode.Enabled = true;
                DownloadResult noConsent = bridge.DownloadAsync("https://example.com/watch?v=two")
                    .GetAwaiter().GetResult();
                Equal(DownloadErrorCode.NoticeNotAccepted, noConsent.ErrorCode, "consent gate");

                config.UrlMode.NoticeAcceptedVersion = 1;
                DownloadResult badUrl = bridge.DownloadAsync("file:///C:/not-a-web-url")
                    .GetAwaiter().GetResult();
                Equal(DownloadErrorCode.InvalidUrl, badUrl.ErrorCode, "web URL gate");
            });
        }

        private static SyncProtocol.Reader Open(
            ArraySegment<byte> frame,
            SyncProtocol.MsgType expectedType)
        {
            var reader = new SyncProtocol.Reader(frame);
            True(reader.Ok, "valid frame header");
            Equal(SyncProtocol.Version, reader.PeerVersion, "protocol version");
            Equal(expectedType, reader.Type, "message type");
            return reader;
        }

        private static byte[] CopyFrame(ArraySegment<byte> frame)
        {
            var copy = new byte[frame.Count];
            Buffer.BlockCopy(frame.Array, frame.Offset, copy, 0, frame.Count);
            return copy;
        }

        private static string Sha256(byte[] content)
        {
            using var sha = SHA256.Create();
            byte[] digest = sha.ComputeHash(content);
            var result = new StringBuilder(digest.Length * 2);
            for (int i = 0; i < digest.Length; i++) result.Append(digest[i].ToString("x2"));
            return result.ToString();
        }

        private static void InTempDirectory(Action<string> action)
        {
            string root = Path.Combine(
                Path.GetTempPath(),
                "MidnightRadio-Smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                action(root);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private static void True(bool value, string context)
        {
            if (!value) throw new InvalidOperationException(context + ": expected true");
        }

        private static void False(bool value, string context)
        {
            if (value) throw new InvalidOperationException(context + ": expected false");
        }

        private static void NotNull(object value, string context)
        {
            if (value == null) throw new InvalidOperationException(context + ": expected a value");
        }

        private static void Equal<T>(T expected, T actual, string context)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidOperationException(
                    $"{context}: expected <{expected}>, got <{actual}>");
            }
        }

        private static void SequenceEqual<T>(
            IEnumerable<T> expected,
            IEnumerable<T> actual,
            string context)
        {
            if (!expected.SequenceEqual(actual))
                throw new InvalidOperationException(context + ": sequences differ");
        }

        private static void Near(float expected, float actual, string context)
        {
            if (Math.Abs(expected - actual) > 0.0001f)
            {
                throw new InvalidOperationException(
                    $"{context}: expected <{expected}>, got <{actual}>");
            }
        }

        private static void Throws<TException>(Action action, string context)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"{context}: expected {typeof(TException).Name}, got {ex.GetType().Name}");
            }

            throw new InvalidOperationException(
                $"{context}: expected {typeof(TException).Name}, no exception was thrown");
        }
    }
}

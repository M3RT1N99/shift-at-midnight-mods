using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MidnightRadio
{
    /// <summary>
    /// Immutable description of one distinct piece of local audio. Identical files are one
    /// track; <see cref="Paths"/> contains every location found during the last scan and
    /// <see cref="Path"/> is the preferred location used for playback.
    /// </summary>
    internal sealed class TrackInfo
    {
        private readonly ReadOnlyCollection<string> _paths;

        internal TrackInfo(
            string id,
            string title,
            IList<string> paths,
            long sizeBytes,
            DateTime lastWriteTimeUtc)
        {
            Id = id ?? string.Empty;
            Title = title ?? string.Empty;
            SizeBytes = sizeBytes;
            LastWriteTimeUtc = lastWriteTimeUtc;

            var copy = paths == null ? Array.Empty<string>() : CopyPaths(paths);
            _paths = Array.AsReadOnly(copy);
            Path = copy.Length == 0 ? string.Empty : copy[0];
        }

        /// <summary>Lower-case SHA-256 of the complete file contents.</summary>
        public string Id { get; }

        /// <summary>Convenience alias for code which names the network identity explicitly.</summary>
        public string ContentHash => Id;

        public string Title { get; }
        public string Path { get; }
        public IReadOnlyList<string> Paths => _paths;
        public long SizeBytes { get; }
        public DateTime LastWriteTimeUtc { get; }

        private static string[] CopyPaths(IList<string> paths)
        {
            var copy = new string[paths.Count];
            for (int i = 0; i < paths.Count; i++) copy[i] = paths[i];
            return copy;
        }
    }

    /// <summary>
    /// Loader- and Unity-independent index of local music. A reload builds a complete new
    /// state off to the side and publishes it in one short lock, so readers always observe
    /// either the old or the new snapshot and never a half-filled library.
    /// </summary>
    internal sealed class MusicLibrary
    {
        private const int IndexVersion = 1;
        private const long MaxIndexBytes = 64L * 1024L * 1024L;

        private static readonly string[] SupportedExtensionValues =
        {
            ".mp3", ".ogg", ".oga", ".wav", ".aiff", ".aif",
        };

        private static readonly HashSet<string> SupportedExtensionSet =
            new HashSet<string>(SupportedExtensionValues, StringComparer.OrdinalIgnoreCase);

        private static readonly StringComparer PathComparer =
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        private static readonly JsonSerializerOptions IndexJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

        private readonly object _reloadGate = new object();
        private readonly object _stateGate = new object();
        private readonly string[] _configuredDirectories;
        private readonly bool _pathsValid;

        private IReadOnlyList<TrackInfo> _snapshot =
            Array.AsReadOnly(Array.Empty<TrackInfo>());

        private Dictionary<string, TrackInfo> _tracksById =
            new Dictionary<string, TrackInfo>(StringComparer.OrdinalIgnoreCase);

        /// <param name="userDataDirectory">
        /// MidnightRadio's user-data root (the directory containing config.json). Relative
        /// entries in Config.MusicDirs are resolved against this directory.
        /// </param>
        public MusicLibrary(string userDataDirectory, Config config)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(userDataDirectory))
                    throw new ArgumentException("user-data directory is empty", nameof(userDataDirectory));

                UserDataDirectory = NormalizePath(userDataDirectory);
                MusicDirectory = NormalizePath(System.IO.Path.Combine(UserDataDirectory, "Music"));
                DownloadCacheDirectory = NormalizePath(
                    System.IO.Path.Combine(UserDataDirectory, "Cache", "_ytdlp"));
                IndexPath = NormalizePath(System.IO.Path.Combine(UserDataDirectory, "library.json"));
                _pathsValid = true;
            }
            catch (Exception ex)
            {
                UserDataDirectory = string.Empty;
                MusicDirectory = string.Empty;
                DownloadCacheDirectory = string.Empty;
                IndexPath = string.Empty;
                _pathsValid = false;
                Log.Error($"music library path setup failed: {ex.GetType().Name}: {ex.Message}");
            }

            _configuredDirectories = CopyConfiguredDirectories(config);
        }

        public string UserDataDirectory { get; }
        public string MusicDirectory { get; }
        public string DownloadCacheDirectory { get; }
        public string IndexPath { get; }

        /// <summary>Extensions accepted by AudioLoader, including the leading dot.</summary>
        public static IReadOnlyCollection<string> SupportedExtensions { get; } =
            Array.AsReadOnly(SupportedExtensionValues);

        /// <summary>An immutable, thread-safe view which stays valid across later reloads.</summary>
        public IReadOnlyList<TrackInfo> Snapshot
        {
            get
            {
                lock (_stateGate) return _snapshot;
            }
        }

        public int Count
        {
            get
            {
                lock (_stateGate) return _snapshot.Count;
            }
        }

        public static bool IsSupportedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return SupportedExtensionSet.Contains(System.IO.Path.GetExtension(path)); }
            catch { return false; }
        }

        /// <summary>
        /// Recursively rescans all roots. The method is synchronous by design; callers that
        /// run on a game thread can put it on a worker. Failures are logged and never escape.
        /// </summary>
        public IReadOnlyList<TrackInfo> Reload()
        {
            lock (_reloadGate)
            {
                if (!_pathsValid) return Snapshot;

                try
                {
                    EnsureDefaultDirectory();

                    var cached = LoadCache();
                    var nextCache = new List<CacheEntry>();
                    var tracks = Scan(cached, nextCache);
                    var nextSnapshot = BuildSnapshot(tracks);
                    var nextById = BuildLookup(nextSnapshot);

                    lock (_stateGate)
                    {
                        _snapshot = nextSnapshot;
                        _tracksById = nextById;
                    }

                    SaveCache(nextCache);
                    Log.Info($"music library: {nextSnapshot.Count} track(s), {nextCache.Count} file(s)");
                    return nextSnapshot;
                }
                catch (Exception ex)
                {
                    // This boundary is deliberately broad. A damaged file or an unusual
                    // filesystem must disable at most one reload, never the mod or game.
                    Log.Error($"music library reload failed: {ex.GetType().Name}: {ex.Message}");
                    return Snapshot;
                }
            }
        }

        public bool TryGetTrack(string trackId, out TrackInfo track)
        {
            track = null;
            if (string.IsNullOrWhiteSpace(trackId)) return false;

            lock (_stateGate)
                return _tracksById.TryGetValue(trackId.Trim(), out track);
        }

        public bool TryGetPath(string trackId, out string path)
        {
            path = null;
            if (!TryGetTrack(trackId, out var track)) return false;
            path = track.Path;
            return !string.IsNullOrEmpty(path);
        }

        private Dictionary<string, MutableTrack> Scan(
            Dictionary<string, CacheEntry> cached,
            List<CacheEntry> nextCache)
        {
            var tracks = new Dictionary<string, MutableTrack>(StringComparer.OrdinalIgnoreCase);
            var seenFiles = new HashSet<string>(PathComparer);

            foreach (var root in GetScanRoots())
            {
                var files = CollectSupportedFiles(root);
                for (int i = 0; i < files.Count; i++)
                {
                    string path = files[i];
                    if (!seenFiles.Add(path)) continue;

                    if (!TryGetFingerprint(path, out long size, out long writeTicks, out string metadataError))
                    {
                        Log.Warn($"cannot inspect music file '{path}': {metadataError}");
                        continue;
                    }

                    string hash = null;
                    if (cached.TryGetValue(path, out var old) &&
                        old.SizeBytes == size &&
                        old.LastWriteUtcTicks == writeTicks &&
                        TryNormalizeHash(old.Sha256, out var cachedHash))
                    {
                        hash = cachedHash;
                    }
                    else if (!TryHashStableFile(path, size, writeTicks, out hash, out size, out writeTicks))
                    {
                        continue;
                    }

                    nextCache.Add(new CacheEntry
                    {
                        Path = path,
                        SizeBytes = size,
                        LastWriteUtcTicks = writeTicks,
                        Sha256 = hash,
                    });

                    if (!tracks.TryGetValue(hash, out var mutable))
                    {
                        mutable = new MutableTrack(
                            hash,
                            System.IO.Path.GetFileNameWithoutExtension(path),
                            size,
                            new DateTime(writeTicks, DateTimeKind.Utc));
                        tracks.Add(hash, mutable);
                    }

                    mutable.Paths.Add(path);
                }
            }

            return tracks;
        }

        private List<string> GetScanRoots()
        {
            var roots = new List<string>();
            var seen = new HashSet<string>(PathComparer);

            AddRoot(MusicDirectory, false, roots, seen);
            // URL downloads intentionally live in a managed cache rather than the user's
            // own Music folder, but they still need to enter the playable library.
            AddRoot(DownloadCacheDirectory, false, roots, seen);
            for (int i = 0; i < _configuredDirectories.Length; i++)
                AddRoot(_configuredDirectories[i], true, roots, seen);

            return roots;
        }

        private void AddRoot(
            string configuredPath,
            bool resolveRelative,
            List<string> roots,
            HashSet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return;

            try
            {
                string path = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
                path = ExpandHome(path);
                if (resolveRelative && !System.IO.Path.IsPathRooted(path))
                    path = System.IO.Path.Combine(UserDataDirectory, path);

                path = NormalizePath(path);
                if (seen.Add(path)) roots.Add(path);
            }
            catch (Exception ex)
            {
                Log.Warn($"invalid music directory '{configuredPath}': {ex.Message}");
            }
        }

        private static List<string> CollectSupportedFiles(string root)
        {
            var found = new List<string>();
            if (!Directory.Exists(root))
            {
                Log.Warn($"music directory does not exist: {root}");
                return found;
            }

            var pending = new Stack<string>();
            var visited = new HashSet<string>(PathComparer);
            pending.Push(root);

            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                if (!visited.Add(directory)) continue;

                string[] files;
                try
                {
                    files = Directory.GetFiles(directory);
                    Array.Sort(files, PathComparer);
                }
                catch (Exception ex)
                {
                    Log.Warn($"cannot scan music directory '{directory}': {ex.Message}");
                    files = Array.Empty<string>();
                }

                for (int i = 0; i < files.Length; i++)
                {
                    if (!IsSupportedPath(files[i])) continue;
                    try { found.Add(NormalizePath(files[i])); }
                    catch (Exception ex)
                    {
                        Log.Warn($"invalid music file path '{files[i]}': {ex.Message}");
                    }
                }

                string[] directories;
                try
                {
                    directories = Directory.GetDirectories(directory);
                    Array.Sort(directories, PathComparer);
                }
                catch (Exception ex)
                {
                    Log.Warn($"cannot enumerate subdirectories of '{directory}': {ex.Message}");
                    directories = Array.Empty<string>();
                }

                // Push in reverse so the stack visits directories in ascending order.
                for (int i = directories.Length - 1; i >= 0; i--)
                {
                    try
                    {
                        if ((File.GetAttributes(directories[i]) & FileAttributes.ReparsePoint) != 0)
                        {
                            Log.Debug($"skipping linked music directory: {directories[i]}");
                            continue;
                        }

                        pending.Push(NormalizePath(directories[i]));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"cannot inspect music directory '{directories[i]}': {ex.Message}");
                    }
                }
            }

            return found;
        }

        private static bool TryGetFingerprint(
            string path,
            out long size,
            out long writeTicks,
            out string error)
        {
            size = 0;
            writeTicks = 0;
            error = null;

            try
            {
                var file = new FileInfo(path);
                file.Refresh();
                if (!file.Exists)
                {
                    error = "file disappeared during scan";
                    return false;
                }

                size = file.Length;
                writeTicks = file.LastWriteTimeUtc.Ticks;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryHashStableFile(
            string path,
            long initialSize,
            long initialWriteTicks,
            out string hash,
            out long stableSize,
            out long stableWriteTicks)
        {
            hash = null;
            stableSize = initialSize;
            stableWriteTicks = initialWriteTicks;

            // A file being copied into the folder can change between enumeration and hash.
            // Retry once, but do not cache an identity if it keeps moving underneath us.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    byte[] digest;
                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        128 * 1024,
                        FileOptions.SequentialScan))
                    using (var sha = SHA256.Create())
                    {
                        digest = sha.ComputeHash(stream);
                    }

                    if (!TryGetFingerprint(path, out long afterSize, out long afterTicks, out string metadataError))
                    {
                        Log.Warn($"cannot verify music file '{path}' after hashing: {metadataError}");
                        return false;
                    }

                    hash = ToLowerHex(digest);
                    if (stableSize == afterSize && stableWriteTicks == afterTicks)
                    {
                        stableSize = afterSize;
                        stableWriteTicks = afterTicks;
                        return true;
                    }

                    stableSize = afterSize;
                    stableWriteTicks = afterTicks;
                }
                catch (Exception ex)
                {
                    Log.Warn($"cannot hash music file '{path}': {ex.GetType().Name}: {ex.Message}");
                    return false;
                }
            }

            Log.Warn($"music file kept changing during scan, skipped: {path}");
            hash = null;
            return false;
        }

        private Dictionary<string, CacheEntry> LoadCache()
        {
            var result = new Dictionary<string, CacheEntry>(PathComparer);
            if (!File.Exists(IndexPath)) return result;

            try
            {
                var info = new FileInfo(IndexPath);
                if (info.Length > MaxIndexBytes)
                {
                    Log.Warn($"music library cache is unexpectedly large ({info.Length} bytes), rebuilding it");
                    return result;
                }

                var json = File.ReadAllText(IndexPath, Encoding.UTF8);
                var index = JsonSerializer.Deserialize<LibraryIndex>(json, IndexJsonOptions);
                if (index == null || index.Version != IndexVersion || index.Files == null)
                {
                    Log.Warn("music library cache has an unsupported format, rebuilding it");
                    return result;
                }

                int rejected = 0;
                for (int i = 0; i < index.Files.Count; i++)
                {
                    var entry = index.Files[i];
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Path) ||
                        entry.SizeBytes < 0 || entry.LastWriteUtcTicks < DateTime.MinValue.Ticks ||
                        entry.LastWriteUtcTicks > DateTime.MaxValue.Ticks ||
                        !TryNormalizeHash(entry.Sha256, out var normalizedHash))
                    {
                        rejected++;
                        continue;
                    }

                    try
                    {
                        entry.Path = NormalizePath(entry.Path);
                        entry.Sha256 = normalizedHash;
                        result[entry.Path] = entry;
                    }
                    catch
                    {
                        rejected++;
                    }
                }

                if (rejected != 0)
                    Log.Warn($"ignored {rejected} invalid music library cache entr{(rejected == 1 ? "y" : "ies")}");
            }
            catch (Exception ex)
            {
                Log.Warn($"music library cache could not be read, rebuilding it ({ex.Message})");
            }

            return result;
        }

        private void SaveCache(List<CacheEntry> files)
        {
            string temporaryPath = null;
            try
            {
                Directory.CreateDirectory(UserDataDirectory);
                files.Sort((a, b) => PathComparer.Compare(a.Path, b.Path));

                var index = new LibraryIndex
                {
                    Version = IndexVersion,
                    Files = files,
                };

                string json = JsonSerializer.Serialize(index, IndexJsonOptions);
                temporaryPath = IndexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    32 * 1024,
                    FileOptions.SequentialScan))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(json);
                    writer.WriteLine();
                    writer.Flush();
                    stream.Flush(true);
                }

                // Both paths live in the same directory. File.Move's overwrite form maps
                // to a replacing rename on the supported .NET 6 platforms, so readers see
                // either the complete old index or the complete new one.
                File.Move(temporaryPath, IndexPath, true);
                temporaryPath = null;
            }
            catch (Exception ex)
            {
                Log.Warn($"music library cache could not be saved: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath))
                {
                    try { File.Delete(temporaryPath); }
                    catch { }
                }
            }
        }

        private void EnsureDefaultDirectory()
        {
            try { Directory.CreateDirectory(MusicDirectory); }
            catch (Exception ex)
            {
                Log.Warn($"default music directory could not be created: {ex.Message}");
            }
        }

        private static IReadOnlyList<TrackInfo> BuildSnapshot(
            Dictionary<string, MutableTrack> tracks)
        {
            var values = new TrackInfo[tracks.Count];
            int position = 0;
            foreach (var mutable in tracks.Values)
            {
                values[position++] = new TrackInfo(
                    mutable.Id,
                    mutable.Title,
                    mutable.Paths,
                    mutable.SizeBytes,
                    mutable.LastWriteTimeUtc);
            }

            Array.Sort(values, CompareTracks);
            return Array.AsReadOnly(values);
        }

        private static int CompareTracks(TrackInfo left, TrackInfo right)
        {
            int title = StringComparer.OrdinalIgnoreCase.Compare(left.Title, right.Title);
            if (title != 0) return title;
            int path = PathComparer.Compare(left.Path, right.Path);
            return path != 0 ? path : StringComparer.Ordinal.Compare(left.Id, right.Id);
        }

        private static Dictionary<string, TrackInfo> BuildLookup(IReadOnlyList<TrackInfo> tracks)
        {
            var result = new Dictionary<string, TrackInfo>(tracks.Count, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tracks.Count; i++) result.Add(tracks[i].Id, tracks[i]);
            return result;
        }

        private static string[] CopyConfiguredDirectories(Config config)
        {
            try
            {
                if (config?.MusicDirs == null || config.MusicDirs.Count == 0)
                    return Array.Empty<string>();

                var copy = new string[config.MusicDirs.Count];
                for (int i = 0; i < copy.Length; i++) copy[i] = config.MusicDirs[i];
                return copy;
            }
            catch (Exception ex)
            {
                Log.Warn($"music directories could not be read from config: {ex.Message}");
                return Array.Empty<string>();
            }
        }

        private static string ExpandHome(string path)
        {
            if (path != "~" && !path.StartsWith("~" + System.IO.Path.DirectorySeparatorChar) &&
                !path.StartsWith("~" + System.IO.Path.AltDirectorySeparatorChar))
                return path;

            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home)) return path;
            return path.Length == 1 ? home : System.IO.Path.Combine(home, path.Substring(2));
        }

        private static string NormalizePath(string path)
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }

        private static bool TryNormalizeHash(string value, out string normalized)
        {
            normalized = null;
            if (value == null || value.Length != 64) return false;

            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper) return false;
            }

            normalized = value.ToLowerInvariant();
            return true;
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 0x0F];
            }

            return new string(chars);
        }

        private sealed class MutableTrack
        {
            public MutableTrack(
                string id,
                string title,
                long sizeBytes,
                DateTime lastWriteTimeUtc)
            {
                Id = id;
                Title = title;
                SizeBytes = sizeBytes;
                LastWriteTimeUtc = lastWriteTimeUtc;
            }

            public string Id { get; }
            public string Title { get; }
            public long SizeBytes { get; }
            public DateTime LastWriteTimeUtc { get; }
            public List<string> Paths { get; } = new List<string>();
        }

        private sealed class LibraryIndex
        {
            public int Version { get; set; }
            public List<CacheEntry> Files { get; set; } = new List<CacheEntry>();
        }

        private sealed class CacheEntry
        {
            public string Path { get; set; }
            public long SizeBytes { get; set; }
            public long LastWriteUtcTicks { get; set; }
            public string Sha256 { get; set; }
        }
    }
}

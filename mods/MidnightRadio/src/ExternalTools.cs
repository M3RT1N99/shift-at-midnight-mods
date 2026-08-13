using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightRadio
{
    /// <summary>
    /// The optional command-line tools known to Midnight Radio. Tool discovery is kept
    /// independent of Unity and of the mod loader: callers only supply the loaded config
    /// and the UserData/MidnightRadio directory.
    /// </summary>
    internal enum ExternalToolKind
    {
        YtDlp,
        Ffmpeg,
    }

    internal enum ToolLookupErrorCode
    {
        None,
        NotFound,
        ValidationFailed,
        Cancelled,
    }

    /// <summary>A tool is usable only after its version command exited successfully.</summary>
    internal sealed class ToolResolution
    {
        private ToolResolution(
            ExternalToolKind kind,
            ToolLookupErrorCode errorCode,
            string executablePath,
            string version,
            string source,
            string message)
        {
            Kind = kind;
            ErrorCode = errorCode;
            ExecutablePath = executablePath;
            Version = version;
            Source = source;
            Message = message;
        }

        public ExternalToolKind Kind { get; }
        public ToolLookupErrorCode ErrorCode { get; }
        public string ExecutablePath { get; }
        public string Version { get; }
        public string Source { get; }
        public string Message { get; }
        public bool Found => ErrorCode == ToolLookupErrorCode.None;

        internal static ToolResolution Success(
            ExternalToolKind kind, string path, string version, string source)
        {
            return new ToolResolution(kind, ToolLookupErrorCode.None, path, version, source, null);
        }

        internal static ToolResolution Failure(
            ExternalToolKind kind, ToolLookupErrorCode code, string message)
        {
            return new ToolResolution(kind, code, null, null, null, message);
        }
    }

    /// <summary>
    /// Finds user-installed tools without installing, downloading, or updating anything.
    /// Candidates are checked in this order: explicit config, MidnightRadio/Tools, PATH,
    /// and finally the Windows package-manager locations declared in mod.json.
    /// </summary>
    internal sealed class ToolLocator
    {
        private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(8);

        private readonly Config _config;
        private readonly string _dataDirectory;
        private readonly TimeSpan _probeTimeout;

        public ToolLocator(Config config, string midnightRadioDataDirectory, TimeSpan? probeTimeout = null)
        {
            _config = config;
            _dataDirectory = NormalizeDirectory(midnightRadioDataDirectory);
            _probeTimeout = probeTimeout.HasValue && probeTimeout.Value > TimeSpan.Zero
                ? probeTimeout.Value
                : DefaultProbeTimeout;
        }

        public Task<ToolResolution> FindYtDlpAsync(CancellationToken cancellationToken = default)
        {
            string explicitPath = _config?.Tools?.YtDlpPath;
            return FindAsync(
                ExternalToolKind.YtDlp,
                explicitPath,
                ExecutableNames("yt-dlp"),
                YtDlpExtraDirectories(),
                new[] { "--ignore-config", "--version" },
                ValidateYtDlpVersion,
                cancellationToken);
        }

        public Task<ToolResolution> FindFfmpegAsync(CancellationToken cancellationToken = default)
        {
            string explicitPath = _config?.Tools?.FfmpegPath;
            return FindAsync(
                ExternalToolKind.Ffmpeg,
                explicitPath,
                ExecutableNames("ffmpeg"),
                FfmpegExtraDirectories(),
                new[] { "-hide_banner", "-version" },
                ValidateFfmpegVersion,
                cancellationToken);
        }

        private async Task<ToolResolution> FindAsync(
            ExternalToolKind kind,
            string explicitPath,
            IReadOnlyList<string> executableNames,
            IEnumerable<string> extraDirectories,
            IReadOnlyList<string> validationArguments,
            Func<ProcessRunResult, string> versionReader,
            CancellationToken cancellationToken)
        {
            var candidates = BuildCandidates(explicitPath, executableNames, extraDirectories);
            var failures = new List<string>();
            bool foundExistingCandidate = false;

            foreach (ToolCandidate candidate in candidates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return ToolResolution.Failure(
                        kind, ToolLookupErrorCode.Cancelled, "Tool search was cancelled.");
                }

                if (!File.Exists(candidate.Path)) continue;
                foundExistingCandidate = true;

                string executablePath = ResolveLinkIfPossible(candidate.Path);
                ProcessRunResult run = await ProcessRunner.RunAsync(
                    executablePath,
                    validationArguments,
                    workingDirectory: null,
                    _probeTimeout,
                    cancellationToken).ConfigureAwait(false);

                if (run.Cancelled)
                {
                    return ToolResolution.Failure(
                        kind, ToolLookupErrorCode.Cancelled, "Tool validation was cancelled.");
                }

                string version = versionReader(run);
                if (!run.TimedOut && run.StartError == null && run.ExitCode == 0 && version != null)
                {
                    Log.Debug($"validated {DisplayName(kind)} {version} at {executablePath} ({candidate.Source})");
                    return ToolResolution.Success(kind, executablePath, version, candidate.Source);
                }

                string reason = run.TimedOut
                    ? "validation timed out"
                    : run.StartError != null
                        ? run.StartError.Message
                        : $"version command exited with code {run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}";
                failures.Add($"{candidate.Path}: {reason}");
                Log.Debug($"rejected {DisplayName(kind)} candidate {candidate.Path}: {reason}");
            }

            if (foundExistingCandidate)
            {
                string details = failures.Count == 0
                    ? "No candidate passed its version check."
                    : "No candidate passed its version check: " + string.Join("; ", failures.Take(3));
                return ToolResolution.Failure(kind, ToolLookupErrorCode.ValidationFailed, details);
            }

            return ToolResolution.Failure(
                kind,
                ToolLookupErrorCode.NotFound,
                $"{DisplayName(kind)} was not found in config, MidnightRadio/Tools, PATH, or the supported Windows tool locations.");
        }

        private IReadOnlyList<ToolCandidate> BuildCandidates(
            string explicitPath,
            IReadOnlyList<string> executableNames,
            IEnumerable<string> extraDirectories)
        {
            var result = new List<ToolCandidate>();
            var seen = new HashSet<string>(PathComparer);

            // An explicit config value wins, but an invalid value does not prevent a safe
            // fallback to another user-installed copy.
            AddExplicitCandidate(result, seen, explicitPath, executableNames);

            if (_dataDirectory != null)
            {
                AddDirectoryCandidates(
                    result,
                    seen,
                    Path.Combine(_dataDirectory, "Tools"),
                    executableNames,
                    "MidnightRadio/Tools");
            }

            string pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (string entry in pathValue.Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0) continue;
                AddDirectoryCandidates(result, seen, directory, executableNames, "PATH");
            }

            foreach (string directory in extraDirectories)
            {
                AddDirectoryCandidates(result, seen, directory, executableNames, "Windows tool location");
            }

            return result;
        }

        private void AddExplicitCandidate(
            List<ToolCandidate> result,
            HashSet<string> seen,
            string configuredPath,
            IReadOnlyList<string> executableNames)
        {
            if (string.IsNullOrWhiteSpace(configuredPath)) return;

            string expanded = ExpandConfiguredPath(configuredPath);
            if (expanded == null) return;

            if (Directory.Exists(expanded) || EndsWithDirectorySeparator(configuredPath))
            {
                AddDirectoryCandidates(result, seen, expanded, executableNames, "config");
                return;
            }

            AddCandidate(result, seen, expanded, "config");
        }

        private static void AddDirectoryCandidates(
            List<ToolCandidate> result,
            HashSet<string> seen,
            string directory,
            IReadOnlyList<string> executableNames,
            string source)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            foreach (string executableName in executableNames)
            {
                try { AddCandidate(result, seen, Path.Combine(directory, executableName), source); }
                catch (Exception ex) when (IsPathException(ex)) { }
            }
        }

        private static void AddCandidate(
            List<ToolCandidate> result,
            HashSet<string> seen,
            string path,
            string source)
        {
            string normalized;
            try { normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
            catch (Exception ex) when (IsPathException(ex)) { return; }

            if (seen.Add(normalized)) result.Add(new ToolCandidate(normalized, source));
        }

        private string ExpandConfiguredPath(string configuredPath)
        {
            try
            {
                string value = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
                if (value.Length == 0) return null;

                if (value == "~" || value.StartsWith("~" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    value.StartsWith("~" + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
                {
                    string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (!string.IsNullOrWhiteSpace(home))
                    {
                        value = value.Length == 1
                            ? home
                            : Path.Combine(home, value.Substring(2));
                    }
                }

                if (!Path.IsPathRooted(value) && _dataDirectory != null)
                    value = Path.Combine(_dataDirectory, value);

                return Path.GetFullPath(value);
            }
            catch (Exception ex) when (IsPathException(ex))
            {
                return null;
            }
        }

        private static IEnumerable<string> YtDlpExtraDirectories()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(userProfile))
                yield return Path.Combine(userProfile, "scoop", "shims");

            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (!string.IsNullOrWhiteSpace(commonAppData))
                yield return Path.Combine(commonAppData, "chocolatey", "bin");
        }

        private static IEnumerable<string> FfmpegExtraDirectories()
        {
            if (!OperatingSystem.IsWindows()) yield break;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
                yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links");
        }

        private static IReadOnlyList<string> ExecutableNames(string baseName)
        {
            return OperatingSystem.IsWindows()
                ? new[] { baseName + ".exe", baseName }
                : new[] { baseName, baseName + ".exe" };
        }

        private static string ValidateYtDlpVersion(ProcessRunResult run)
        {
            if (run.ExitCode != 0) return null;
            string line = FirstNonEmptyLine(run.StandardOutput) ?? FirstNonEmptyLine(run.StandardError);
            if (line == null || line.Length > 128 || !line.Any(char.IsDigit)) return null;
            return line;
        }

        private static string ValidateFfmpegVersion(ProcessRunResult run)
        {
            if (run.ExitCode != 0) return null;
            string combined = (run.StandardOutput ?? string.Empty) + "\n" + (run.StandardError ?? string.Empty);
            if (combined.IndexOf("ffmpeg version", StringComparison.OrdinalIgnoreCase) < 0) return null;
            return FirstNonEmptyLine(combined);
        }

        private static string FirstNonEmptyLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            using var reader = new StringReader(value);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                line = line.Trim();
                if (line.Length != 0) return line;
            }
            return null;
        }

        private static string ResolveLinkIfPossible(string path)
        {
            try
            {
                FileSystemInfo target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
                return target != null && File.Exists(target.FullName)
                    ? Path.GetFullPath(target.FullName)
                    : path;
            }
            catch
            {
                return path;
            }
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
            catch (Exception ex) when (IsPathException(ex)) { return null; }
        }

        private static bool EndsWithDirectorySeparator(string path)
        {
            string trimmed = path.Trim().Trim('"');
            return trimmed.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
                   trimmed.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal);
        }

        private static bool IsPathException(Exception ex)
        {
            return ex is ArgumentException || ex is NotSupportedException ||
                   ex is PathTooLongException || ex is System.Security.SecurityException;
        }

        private static StringComparer PathComparer => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

        private static string DisplayName(ExternalToolKind kind)
        {
            return kind == ExternalToolKind.YtDlp ? "yt-dlp" : "ffmpeg";
        }

        private sealed class ToolCandidate
        {
            public ToolCandidate(string path, string source)
            {
                Path = path;
                Source = source;
            }

            public string Path { get; }
            public string Source { get; }
        }
    }

    internal enum DownloadErrorCode
    {
        None,
        UrlModeDisabled,
        NoticeNotAccepted,
        InvalidUrl,
        InvalidDataDirectory,
        YtDlpNotFound,
        YtDlpValidationFailed,
        FfmpegNotFound,
        FfmpegValidationFailed,
        CacheDirectoryUnavailable,
        CacheLimitExceeded,
        DurationLimitExceeded,
        ToolStartFailed,
        ToolFailed,
        TimedOut,
        Cancelled,
        OutputMissing,
        OutputOutsideCache,
        OutputInvalid,
    }

    /// <summary>Progress from yt-dlp. Every value is optional because not every site reports it.</summary>
    internal sealed class DownloadProgress
    {
        public DownloadProgress(
            long? downloadedBytes,
            long? totalBytes,
            double? bytesPerSecond,
            TimeSpan? estimatedRemaining,
            double? percent)
        {
            DownloadedBytes = downloadedBytes;
            TotalBytes = totalBytes;
            BytesPerSecond = bytesPerSecond;
            EstimatedRemaining = estimatedRemaining;
            Percent = percent;
        }

        public long? DownloadedBytes { get; }
        public long? TotalBytes { get; }
        public double? BytesPerSecond { get; }
        public TimeSpan? EstimatedRemaining { get; }
        public double? Percent { get; }
    }

    /// <summary>
    /// A successful result always contains at least one canonical, existing file below
    /// Cache/_ytdlp. AudioPaths can contain more than one item only when playlists are
    /// explicitly allowed; AudioPath is the first item for single-track callers.
    /// </summary>
    internal sealed class DownloadResult
    {
        private DownloadResult(
            DownloadErrorCode errorCode,
            IReadOnlyList<string> audioPaths,
            string message,
            int? processExitCode)
        {
            ErrorCode = errorCode;
            AudioPaths = audioPaths ?? Array.Empty<string>();
            Message = message;
            ProcessExitCode = processExitCode;
        }

        public DownloadErrorCode ErrorCode { get; }
        public IReadOnlyList<string> AudioPaths { get; }
        public string AudioPath => AudioPaths.Count == 0 ? null : AudioPaths[0];
        public string Message { get; }
        public int? ProcessExitCode { get; }
        public bool Success => ErrorCode == DownloadErrorCode.None;

        internal static DownloadResult Succeeded(IReadOnlyList<string> paths, int? exitCode)
        {
            return new DownloadResult(
                DownloadErrorCode.None,
                paths,
                paths.Count == 1 ? "Audio downloaded." : $"Downloaded {paths.Count} audio files.",
                exitCode);
        }

        internal static DownloadResult Failed(
            DownloadErrorCode code, string message, int? exitCode = null)
        {
            return new DownloadResult(code, Array.Empty<string>(), message, exitCode);
        }
    }

    /// <summary>
    /// Opt-in yt-dlp bridge. Calling DownloadAsync is the only operation here that can use
    /// the network. It never installs or updates yt-dlp/ffmpeg and never uses a shell.
    /// </summary>
    internal sealed class YtDlpBridge
    {
        private const string FileMarker = "__MIDNIGHTRADIO_FILE__";
        private const string ProgressMarker = "__MIDNIGHTRADIO_PROGRESS__";

        private readonly Config _config;
        private readonly string _dataDirectory;
        private readonly ToolLocator _toolLocator;

        public YtDlpBridge(Config config, string midnightRadioDataDirectory)
            : this(config, midnightRadioDataDirectory, null)
        {
        }

        internal YtDlpBridge(
            Config config,
            string midnightRadioDataDirectory,
            ToolLocator toolLocator)
        {
            _config = config;
            _dataDirectory = NormalizeDirectory(midnightRadioDataDirectory);
            _toolLocator = toolLocator ?? new ToolLocator(config, midnightRadioDataDirectory);
        }

        public async Task<DownloadResult> DownloadAsync(
            string url,
            IProgress<DownloadProgress> progress = null,
            CancellationToken cancellationToken = default)
        {
            Config.UrlCfg urlConfig = _config?.UrlMode;
            if (urlConfig == null || !urlConfig.Enabled)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.UrlModeDisabled,
                    "URL downloads are disabled in config (urlMode.enabled)."
                );
            }

            if (urlConfig.NoticeAcceptedVersion < 1)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.NoticeNotAccepted,
                    "The URL-download notice has not been accepted (noticeAcceptedVersion must be at least 1)."
                );
            }

            if (cancellationToken.IsCancellationRequested)
                return DownloadResult.Failed(DownloadErrorCode.Cancelled, "Download was cancelled.");

            if (!TryValidateWebUrl(url, out string normalizedUrl))
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.InvalidUrl,
                    "Only absolute HTTP or HTTPS URLs are accepted."
                );
            }

            if (_dataDirectory == null)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.InvalidDataDirectory,
                    "The UserData/MidnightRadio directory is invalid."
                );
            }

            // Snapshot mutable config values before crossing any async boundary. The
            // configured resolve timeout covers discovery, download, and post-processing.
            bool allowPlaylists = urlConfig.AllowPlaylists;
            int maxDurationMinutes = Math.Min(Math.Max(urlConfig.MaxDurationMinutes, 1), 24 * 60);
            int timeoutSeconds = Math.Min(Math.Max(urlConfig.ResolveTimeoutSeconds, 10), 900);
            string audioFormat = NormalizeAudioFormat(urlConfig.AudioFormat);
            int audioQuality = Math.Min(Math.Max(urlConfig.AudioQuality, 0), 10);

            using var operationTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, operationTimeoutCts.Token);
            CancellationToken operationToken = operationCts.Token;

            Task<ToolResolution> ytDlpTask = _toolLocator.FindYtDlpAsync(operationToken);
            Task<ToolResolution> ffmpegTask = _toolLocator.FindFfmpegAsync(operationToken);
            await Task.WhenAll(ytDlpTask, ffmpegTask).ConfigureAwait(false);

            ToolResolution ytDlp = ytDlpTask.Result;
            ToolResolution ffmpeg = ffmpegTask.Result;

            DownloadResult unavailable = ToolFailure(
                ytDlp,
                ffmpeg,
                cancellationToken.IsCancellationRequested,
                operationTimeoutCts.IsCancellationRequested,
                timeoutSeconds);
            if (unavailable != null) return unavailable;

            string cacheDirectory;
            try
            {
                cacheDirectory = Path.GetFullPath(Path.Combine(_dataDirectory, "Cache", "_ytdlp"));
                Directory.CreateDirectory(cacheDirectory);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is ArgumentException || ex is NotSupportedException)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.CacheDirectoryUnavailable,
                    $"Could not create Cache/_ytdlp: {ex.Message}"
                );
            }

            var arguments = BuildDownloadArguments(
                normalizedUrl,
                cacheDirectory,
                ffmpeg.ExecutablePath,
                allowPlaylists,
                maxDurationMinutes,
                audioFormat,
                audioQuality,
                progress != null);

            var reportedPaths = new List<string>();
            object pathLock = new object();

            void HandleToolLine(string line)
            {
                if (line.StartsWith(FileMarker, StringComparison.Ordinal))
                {
                    string path = line.Substring(FileMarker.Length).Trim();
                    if (path.Length != 0)
                    {
                        lock (pathLock) reportedPaths.Add(path);
                    }
                }
                else if (progress != null && line.StartsWith(ProgressMarker, StringComparison.Ordinal))
                {
                    DownloadProgress parsed = ParseProgress(line.Substring(ProgressMarker.Length));
                    if (parsed != null)
                    {
                        try { progress.Report(parsed); }
                        catch { /* A UI progress callback must not kill the download. */ }
                    }
                }
            }

            ProcessRunResult run = await ProcessRunner.RunAsync(
                ytDlp.ExecutablePath,
                arguments,
                cacheDirectory,
                TimeSpan.FromSeconds(timeoutSeconds),
                operationToken,
                standardOutputLine: HandleToolLine,
                standardErrorLine: HandleToolLine).ConfigureAwait(false);

            if (run.Cancelled)
            {
                return cancellationToken.IsCancellationRequested
                    ? DownloadResult.Failed(DownloadErrorCode.Cancelled, "Download was cancelled.")
                    : DownloadResult.Failed(
                        DownloadErrorCode.TimedOut,
                        $"yt-dlp did not finish within {timeoutSeconds} seconds.");
            }
            if (run.TimedOut)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.TimedOut,
                    $"yt-dlp did not finish within {timeoutSeconds} seconds."
                );
            }
            if (run.StartError != null)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.ToolStartFailed,
                    $"yt-dlp could not be started: {run.StartError.Message}"
                );
            }

            string toolOutput = (run.StandardError ?? string.Empty) + "\n" + (run.StandardOutput ?? string.Empty);
            if (run.ExitCode != 0)
            {
                if (LooksLikeDurationRejection(toolOutput))
                {
                    return DownloadResult.Failed(
                        DownloadErrorCode.DurationLimitExceeded,
                        $"The URL was rejected by the {maxDurationMinutes}-minute duration limit.",
                        run.ExitCode);
                }

                string detail = LastMeaningfulLine(run.StandardError) ??
                                LastMeaningfulLine(run.StandardOutput) ??
                                "yt-dlp reported an unspecified error.";
                return DownloadResult.Failed(
                    DownloadErrorCode.ToolFailed,
                    $"yt-dlp failed: {detail}",
                    run.ExitCode);
            }

            List<string> pathSnapshot;
            lock (pathLock) pathSnapshot = reportedPaths.ToList();

            if (pathSnapshot.Count == 0)
            {
                if (LooksLikeDurationRejection(toolOutput))
                {
                    return DownloadResult.Failed(
                        DownloadErrorCode.DurationLimitExceeded,
                        $"The URL was rejected by the {maxDurationMinutes}-minute duration limit.",
                        run.ExitCode);
                }
                return DownloadResult.Failed(
                    DownloadErrorCode.OutputMissing,
                    "yt-dlp exited successfully but did not report a final audio file.",
                    run.ExitCode);
            }

            var verifiedPaths = new List<string>();
            var seenPaths = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

            foreach (string reportedPath in pathSnapshot)
            {
                OutputVerification verification = VerifyOutputPath(cacheDirectory, reportedPath);
                if (verification.ErrorCode != DownloadErrorCode.None)
                    return DownloadResult.Failed(verification.ErrorCode, verification.Message, run.ExitCode);
                if (seenPaths.Add(verification.Path)) verifiedPaths.Add(verification.Path);
            }

            if (verifiedPaths.Count == 0)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.OutputMissing,
                    "No final audio file was produced.",
                    run.ExitCode);
            }

            DownloadResult cacheLimit = EnforceCacheLimit(
                cacheDirectory,
                verifiedPaths,
                _config?.Cache);
            if (cacheLimit != null)
            {
                DeleteNewFiles(verifiedPaths);
                return cacheLimit;
            }

            if (cancellationToken.IsCancellationRequested)
                return DownloadResult.Failed(DownloadErrorCode.Cancelled, "Download was cancelled.");
            if (operationTimeoutCts.IsCancellationRequested)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.TimedOut,
                    $"yt-dlp did not finish within {timeoutSeconds} seconds.");
            }

            return DownloadResult.Succeeded(verifiedPaths.AsReadOnly(), run.ExitCode);
        }

        private static DownloadResult EnforceCacheLimit(
            string cacheDirectory,
            IReadOnlyList<string> newFiles,
            Config.CacheCfg cacheConfig)
        {
            if (cacheConfig == null) return null;

            long maximumBytes = Math.Max(64L, cacheConfig.MaxCacheMB) * 1024L * 1024L;
            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(cacheDirectory).GetFiles("*", SearchOption.TopDirectoryOnly);
                long total = files.Sum(file => file.Length);
                if (total <= maximumBytes) return null;

                var protectedPaths = new HashSet<string>(newFiles, OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

                if (cacheConfig.EvictionEnabled)
                {
                    foreach (FileInfo candidate in files
                                 .Where(file => !protectedPaths.Contains(file.FullName))
                                 .OrderBy(file => file.LastWriteTimeUtc))
                    {
                        long candidateLength = candidate.Length;
                        try { candidate.Delete(); }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            Log.Debug($"could not evict cache file '{candidate.FullName}': {ex.Message}");
                            continue;
                        }
                        total -= candidateLength;
                        if (total <= maximumBytes) break;
                    }
                }

                if (total <= maximumBytes) return null;

                return DownloadResult.Failed(
                    DownloadErrorCode.CacheLimitExceeded,
                    $"The download would exceed the configured {cacheConfig.MaxCacheMB} MB cache limit.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return DownloadResult.Failed(
                    DownloadErrorCode.CacheDirectoryUnavailable,
                    "The download cache could not be inspected: " + ex.Message);
            }
        }

        private static void DeleteNewFiles(IEnumerable<string> paths)
        {
            foreach (string path in paths)
            {
                try { File.Delete(path); }
                catch { }
            }
        }

        private static IReadOnlyList<string> BuildDownloadArguments(
            string url,
            string cacheDirectory,
            string ffmpegPath,
            bool allowPlaylists,
            int maxDurationMinutes,
            string audioFormat,
            int audioQuality,
            bool reportProgress)
        {
            var args = new List<string>
            {
                // Ignore both system and user yt-dlp config files: only arguments assembled
                // here may affect where files are written or which helpers are executed.
                "--ignore-config",
                "--no-simulate",
                "--no-colors",
                "--encoding", "utf-8",
                "--format", "bestaudio/best",
                "--extract-audio",
                "--audio-format", audioFormat,
                "--audio-quality", audioQuality.ToString(CultureInfo.InvariantCulture),
                "--ffmpeg-location", ffmpegPath,
                "--paths", cacheDirectory,
                "--output", "%(extractor_key).32s-%(id).96s.%(ext)s",
                "--restrict-filenames",
                "--windows-filenames",
                "--trim-filenames", "160",
                "--match-filters", $"!is_live & duration <= {maxDurationMinutes * 60}",
                "--print", "after_move:" + FileMarker + "%(filepath)s",
            };

            if (!allowPlaylists) args.Add("--no-playlist");

            if (reportProgress)
            {
                args.Add("--progress");
                args.Add("--newline");
                args.Add("--progress-template");
                args.Add("download:" + ProgressMarker +
                         "%(progress.downloaded_bytes)s|%(progress.total_bytes)s|" +
                         "%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s");
            }
            else
            {
                args.Add("--no-progress");
            }

            // ArgumentList (inside ProcessRunner) keeps this URL a single literal argument.
            args.Add("--");
            args.Add(url);
            return args;
        }

        private static DownloadResult ToolFailure(
            ToolResolution ytDlp,
            ToolResolution ffmpeg,
            bool callerCancelled,
            bool operationTimedOut,
            int timeoutSeconds)
        {
            if (ytDlp.ErrorCode == ToolLookupErrorCode.Cancelled ||
                ffmpeg.ErrorCode == ToolLookupErrorCode.Cancelled)
            {
                return callerCancelled || !operationTimedOut
                    ? DownloadResult.Failed(DownloadErrorCode.Cancelled, "Tool discovery was cancelled.")
                    : DownloadResult.Failed(
                        DownloadErrorCode.TimedOut,
                        $"Tool discovery did not finish within {timeoutSeconds} seconds.");
            }

            if (!ytDlp.Found)
            {
                return DownloadResult.Failed(
                    ytDlp.ErrorCode == ToolLookupErrorCode.NotFound
                        ? DownloadErrorCode.YtDlpNotFound
                        : DownloadErrorCode.YtDlpValidationFailed,
                    ytDlp.Message);
            }

            if (!ffmpeg.Found)
            {
                return DownloadResult.Failed(
                    ffmpeg.ErrorCode == ToolLookupErrorCode.NotFound
                        ? DownloadErrorCode.FfmpegNotFound
                        : DownloadErrorCode.FfmpegValidationFailed,
                    ffmpeg.Message);
            }

            return null;
        }

        private static bool TryValidateWebUrl(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrWhiteSpace(value) ||
                !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                string.IsNullOrWhiteSpace(uri.Host))
            {
                return false;
            }

            normalized = uri.AbsoluteUri;
            return true;
        }

        private static string NormalizeAudioFormat(string value)
        {
            string format = string.IsNullOrWhiteSpace(value)
                ? "vorbis"
                : value.Trim().ToLowerInvariant();
            switch (format)
            {
                case "mp3":
                case "vorbis":
                case "wav":
                    return format;
                default:
                    Log.Warn($"urlMode.audioFormat '{value}' cannot be decoded by the current " +
                             "audio loader, using Ogg Vorbis");
                    return "vorbis";
            }
        }

        private static DownloadProgress ParseProgress(string value)
        {
            string[] parts = value.Split('|');
            if (parts.Length != 5) return null;

            long? downloaded = ParseLong(parts[0]);
            long? total = ParseLong(parts[1]) ?? ParseLong(parts[2]);
            double? speed = ParseDouble(parts[3]);
            double? etaSeconds = ParseDouble(parts[4]);
            TimeSpan? eta = etaSeconds.HasValue && etaSeconds.Value >= 0
                ? TimeSpan.FromSeconds(etaSeconds.Value)
                : null;
            double? percent = downloaded.HasValue && total.HasValue && total.Value > 0
                ? Math.Min(100d, Math.Max(0d, downloaded.Value * 100d / total.Value))
                : null;

            if (!downloaded.HasValue && !total.HasValue && !speed.HasValue && !eta.HasValue)
                return null;
            return new DownloadProgress(downloaded, total, speed, eta, percent);
        }

        private static long? ParseLong(string value)
        {
            return long.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) && parsed >= 0
                ? parsed
                : null;
        }

        private static double? ParseDouble(string value)
        {
            return double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
                   !double.IsNaN(parsed) && !double.IsInfinity(parsed) && parsed >= 0
                ? parsed
                : null;
        }

        private static bool LooksLikeDurationRejection(string output)
        {
            return output.IndexOf("does not pass filter", StringComparison.OrdinalIgnoreCase) >= 0 &&
                   output.IndexOf("duration", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string LastMeaningfulLine(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string[] lines = value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(ProgressMarker, StringComparison.Ordinal)) continue;
                return line.Length <= 500 ? line : line.Substring(0, 500);
            }
            return null;
        }

        private static OutputVerification VerifyOutputPath(string cacheDirectory, string reportedPath)
        {
            string fullPath;
            try
            {
                fullPath = Path.IsPathRooted(reportedPath)
                    ? Path.GetFullPath(reportedPath)
                    : Path.GetFullPath(Path.Combine(cacheDirectory, reportedPath));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException)
            {
                return OutputVerification.Failure(
                    DownloadErrorCode.OutputInvalid,
                    "yt-dlp reported an invalid output path.");
            }

            if (!IsBelowDirectory(cacheDirectory, fullPath))
            {
                return OutputVerification.Failure(
                    DownloadErrorCode.OutputOutsideCache,
                    "yt-dlp reported an output path outside Cache/_ytdlp.");
            }

            if (!File.Exists(fullPath))
            {
                return OutputVerification.Failure(
                    DownloadErrorCode.OutputMissing,
                    "The final audio file reported by yt-dlp does not exist.");
            }

            try
            {
                FileInfo info = new FileInfo(fullPath);
                FileSystemInfo linkTarget = info.ResolveLinkTarget(returnFinalTarget: true);
                if (linkTarget != null)
                {
                    string resolved = Path.GetFullPath(linkTarget.FullName);
                    if (!IsBelowDirectory(cacheDirectory, resolved))
                    {
                        return OutputVerification.Failure(
                            DownloadErrorCode.OutputOutsideCache,
                            "The final audio file is a link to a path outside Cache/_ytdlp.");
                    }
                    fullPath = resolved;
                    info = new FileInfo(fullPath);
                }

                string extension = info.Extension.ToLowerInvariant();
                if (extension != ".ogg" && extension != ".oga" &&
                    extension != ".wav" && extension != ".mp3")
                {
                    return OutputVerification.Failure(
                        DownloadErrorCode.OutputInvalid,
                        $"yt-dlp produced unsupported audio extension '{extension}'.");
                }

                if (info.Length <= 0)
                {
                    return OutputVerification.Failure(
                        DownloadErrorCode.OutputInvalid,
                        "yt-dlp produced an empty audio file.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException ||
                                       ex is NotSupportedException)
            {
                return OutputVerification.Failure(
                    DownloadErrorCode.OutputInvalid,
                    $"The final audio file could not be inspected: {ex.Message}");
            }

            return OutputVerification.Success(fullPath);
        }

        private static bool IsBelowDirectory(string directory, string path)
        {
            string root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            StringComparison comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return path.StartsWith(root, comparison);
        }

        private static string NormalizeDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException ||
                                       ex is PathTooLongException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        private sealed class OutputVerification
        {
            private OutputVerification(DownloadErrorCode errorCode, string path, string message)
            {
                ErrorCode = errorCode;
                Path = path;
                Message = message;
            }

            public DownloadErrorCode ErrorCode { get; }
            public string Path { get; }
            public string Message { get; }

            public static OutputVerification Success(string path)
            {
                return new OutputVerification(DownloadErrorCode.None, path, null);
            }

            public static OutputVerification Failure(DownloadErrorCode code, string message)
            {
                return new OutputVerification(code, null, message);
            }
        }
    }

    /// <summary>Small, bounded process host shared by probing and downloads.</summary>
    internal static class ProcessRunner
    {
        private const int CaptureLimitCharacters = 64 * 1024;

        public static async Task<ProcessRunResult> RunAsync(
            string executablePath,
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            Action<string> standardOutputLine = null,
            Action<string> standardErrorLine = null)
        {
            if (cancellationToken.IsCancellationRequested)
                return ProcessRunResult.WasCancelled();

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (!string.IsNullOrWhiteSpace(workingDirectory))
                startInfo.WorkingDirectory = workingDirectory;
            foreach (string argument in arguments)
                startInfo.ArgumentList.Add(argument);

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            var stdoutDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            process.OutputDataReceived += (object _, DataReceivedEventArgs eventArgs) =>
            {
                if (eventArgs.Data == null)
                {
                    stdoutDone.TrySetResult(true);
                    return;
                }
                AppendBounded(stdout, eventArgs.Data);
                if (standardOutputLine != null)
                {
                    try { standardOutputLine(eventArgs.Data); }
                    catch { }
                }
            };
            process.ErrorDataReceived += (object _, DataReceivedEventArgs eventArgs) =>
            {
                if (eventArgs.Data == null)
                {
                    stderrDone.TrySetResult(true);
                    return;
                }
                AppendBounded(stderr, eventArgs.Data);
                if (standardErrorLine != null)
                {
                    try { standardErrorLine(eventArgs.Data); }
                    catch { }
                }
            };

            bool started = false;
            try
            {
                started = process.Start();
                if (!started)
                    return ProcessRunResult.StartFailed(new InvalidOperationException("Process.Start returned false."));
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                if (started)
                {
                    KillProcessTree(process);
                    await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
                }
                return ProcessRunResult.StartFailed(ex);
            }

            using var timeoutCts = new CancellationTokenSource();
            timeoutCts.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromSeconds(1));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
                await WaitBrieflyForStreamsAsync(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);

                string cancelledStdout = ReadBuilder(stdout);
                string cancelledStderr = ReadBuilder(stderr);
                return cancellationToken.IsCancellationRequested
                    ? ProcessRunResult.WasCancelled(cancelledStdout, cancelledStderr)
                    : ProcessRunResult.WasTimedOut(cancelledStdout, cancelledStderr);
            }
            catch (Exception ex)
            {
                KillProcessTree(process);
                await WaitBrieflyForExitAsync(process).ConfigureAwait(false);
                await WaitBrieflyForStreamsAsync(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
                return ProcessRunResult.StartFailed(ex);
            }

            // A successful process exit is not enough: async line events can still be
            // queued. In particular yt-dlp emits the final after_move filepath at the end,
            // and truncating it turns a valid download into OutputMissing. Drain to EOF,
            // while keeping the same caller/operation timeout as the hard upper bound.
            try
            {
                await Task.WhenAll(stdoutDone.Task, stderrDone.Task)
                    .WaitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillProcessTree(process);
                string cancelledStdout = ReadBuilder(stdout);
                string cancelledStderr = ReadBuilder(stderr);
                return cancellationToken.IsCancellationRequested
                    ? ProcessRunResult.WasCancelled(cancelledStdout, cancelledStderr)
                    : ProcessRunResult.WasTimedOut(cancelledStdout, cancelledStderr);
            }
            return ProcessRunResult.Completed(
                process.ExitCode,
                ReadBuilder(stdout),
                ReadBuilder(stderr));
        }

        private static void KillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                return;
            }
            catch { }

            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch { }
        }

        private static async Task WaitBrieflyForExitAsync(Process process)
        {
            try
            {
                Task wait = process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task WaitBrieflyForStreamsAsync(Task stdout, Task stderr)
        {
            try
            {
                Task streams = Task.WhenAll(stdout, stderr);
                await Task.WhenAny(streams, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            }
            catch { }
        }

        private static void AppendBounded(StringBuilder builder, string line)
        {
            lock (builder)
            {
                builder.AppendLine(line);
                if (builder.Length > CaptureLimitCharacters)
                    builder.Remove(0, builder.Length - CaptureLimitCharacters);
            }
        }

        private static string ReadBuilder(StringBuilder builder)
        {
            lock (builder) return builder.ToString();
        }
    }

    internal sealed class ProcessRunResult
    {
        private ProcessRunResult(
            int? exitCode,
            string standardOutput,
            string standardError,
            bool timedOut,
            bool cancelled,
            Exception startError)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput;
            StandardError = standardError;
            TimedOut = timedOut;
            Cancelled = cancelled;
            StartError = startError;
        }

        public int? ExitCode { get; }
        public string StandardOutput { get; }
        public string StandardError { get; }
        public bool TimedOut { get; }
        public bool Cancelled { get; }
        public Exception StartError { get; }

        internal static ProcessRunResult Completed(int exitCode, string stdout, string stderr)
        {
            return new ProcessRunResult(exitCode, stdout, stderr, false, false, null);
        }

        internal static ProcessRunResult WasTimedOut(string stdout, string stderr)
        {
            return new ProcessRunResult(null, stdout, stderr, true, false, null);
        }

        internal static ProcessRunResult WasCancelled(string stdout = "", string stderr = "")
        {
            return new ProcessRunResult(null, stdout, stderr, false, true, null);
        }

        internal static ProcessRunResult StartFailed(Exception error)
        {
            return new ProcessRunResult(null, string.Empty, string.Empty, false, false, error);
        }
    }
}

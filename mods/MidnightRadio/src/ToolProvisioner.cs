using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightRadio
{
    /// <summary>
    /// Fetches yt-dlp and ffmpeg on first use so nobody has to install anything by hand.
    ///
    /// Downloaded rather than bundled, deliberately:
    ///  - ffmpeg would add roughly 80 MB to a package that is otherwise 64 KB;
    ///  - yt-dlp goes stale within weeks and then simply stops working, so a copy frozen
    ///    at release time is worse than useless;
    ///  - redistributing someone else's binaries carries licence obligations that fetching
    ///    them from their own release pages does not.
    ///
    /// Both land in UserData/MidnightRadio/Tools, which ToolLocator already searches, so
    /// nothing else needs to know where they came from. Failure is never fatal: local
    /// playback of already-playable files works with no tools at all.
    /// </summary>
    internal sealed class ToolProvisioner
    {
        // Official release endpoints. yt-dlp publishes a plain .exe; BtbN publishes the
        // static ffmpeg builds most Windows tooling uses.
        private const string YtDlpUrl =
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string FfmpegUrl =
            "https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip";

        private readonly string _toolsDirectory;
        private readonly SemaphoreSlim _oneAtATime = new(1, 1);

        public ToolProvisioner(string dataDirectory)
        {
            _toolsDirectory = Path.Combine(dataDirectory, "Tools");
        }

        public string YtDlpPath => Path.Combine(_toolsDirectory, "yt-dlp.exe");
        public string FfmpegPath => Path.Combine(_toolsDirectory, "ffmpeg.exe");
        public string FfprobePath => Path.Combine(_toolsDirectory, "ffprobe.exe");

        public bool HasYtDlp => File.Exists(YtDlpPath);
        public bool HasFfmpeg => File.Exists(FfmpegPath);

        /// <summary>Downloads yt-dlp if it is not already present. Returns its path, or null.</summary>
        public async Task<string> EnsureYtDlpAsync(
            IProgress<string> status, CancellationToken cancellationToken)
        {
            if (HasYtDlp) return YtDlpPath;

            return await Serialise(async () =>
            {
                if (HasYtDlp) return YtDlpPath;

                status?.Report("Lade yt-dlp herunter …");
                Directory.CreateDirectory(_toolsDirectory);

                if (!await DownloadToFileAsync(YtDlpUrl, YtDlpPath, status, cancellationToken)
                        .ConfigureAwait(false))
                    return null;

                Log.Info("yt-dlp downloaded");
                status?.Report("yt-dlp bereit.");
                return YtDlpPath;
            }).ConfigureAwait(false);
        }

        /// <summary>Downloads and unpacks ffmpeg if it is not already present.</summary>
        public async Task<string> EnsureFfmpegAsync(
            IProgress<string> status, CancellationToken cancellationToken)
        {
            if (HasFfmpeg) return FfmpegPath;

            return await Serialise(async () =>
            {
                if (HasFfmpeg) return FfmpegPath;

                status?.Report("Lade ffmpeg herunter (etwa 80 MB, einmalig) …");
                Directory.CreateDirectory(_toolsDirectory);

                string archive = Path.Combine(_toolsDirectory, "ffmpeg-download.zip");
                try
                {
                    if (!await DownloadToFileAsync(FfmpegUrl, archive, status, cancellationToken)
                            .ConfigureAwait(false))
                        return null;

                    status?.Report("Entpacke ffmpeg …");

                    // The archive nests everything under a versioned folder, so the two
                    // executables are located by name rather than by a fixed path.
                    using (var zip = ZipFile.OpenRead(archive))
                    {
                        foreach (var entry in zip.Entries)
                        {
                            string name = Path.GetFileName(entry.FullName);
                            if (!string.Equals(name, "ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(name, "ffprobe.exe", StringComparison.OrdinalIgnoreCase))
                                continue;

                            string target = Path.Combine(_toolsDirectory, name);
                            entry.ExtractToFile(target, overwrite: true);
                        }
                    }

                    if (!HasFfmpeg)
                    {
                        Log.Warn("the ffmpeg archive contained no ffmpeg.exe");
                        status?.Report("ffmpeg konnte nicht entpackt werden.");
                        return null;
                    }

                    Log.Info("ffmpeg downloaded and unpacked");
                    status?.Report("ffmpeg bereit.");
                    return FfmpegPath;
                }
                finally
                {
                    try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                }
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Keeps the tools current. Called on start; never blocks the game and never
        /// reports a problem to the player, because being offline is not an error here.
        ///
        /// yt-dlp is checked every start: it breaks within weeks as sites change, and the
        /// check is one small API call. ffmpeg is refreshed on an interval instead - its
        /// build has no cheap version endpoint and re-fetching is ~80 MB.
        /// </summary>
        public async Task UpdateAsync(
            Config.ToolsCfg settings, IProgress<string> status, CancellationToken cancellationToken)
        {
            if (settings != null && !settings.AutoUpdate) return;

            try
            {
                if (HasYtDlp) await UpdateYtDlpAsync(status, cancellationToken).ConfigureAwait(false);

                int refreshDays = Math.Max(1, settings?.FfmpegRefreshDays ?? 30);
                if (HasFfmpeg && OlderThan(FfmpegPath, TimeSpan.FromDays(refreshDays)))
                {
                    Log.Info($"ffmpeg is older than {refreshDays} days, refreshing");
                    TryDelete(FfmpegPath);
                    TryDelete(FfprobePath);
                    await EnsureFfmpegAsync(status, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Debug("tool update check failed: " + ex.Message);
            }
        }

        private async Task UpdateYtDlpAsync(IProgress<string> status, CancellationToken cancellationToken)
        {
            string installed = await ReadYtDlpVersionAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(installed)) return;

            string latest = await ReadLatestYtDlpTagAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(latest)) return;   // offline, rate-limited: keep what we have

            if (string.Equals(installed, latest, StringComparison.OrdinalIgnoreCase))
            {
                Log.Debug($"yt-dlp {installed} is current");
                return;
            }

            Log.Info($"updating yt-dlp {installed} -> {latest}");
            status?.Report($"Aktualisiere yt-dlp auf {latest} …");

            // Downloaded to .part and renamed on success, so a failed update leaves the
            // working copy in place rather than a broken one.
            if (await DownloadToFileAsync(YtDlpUrl, YtDlpPath, status, cancellationToken)
                    .ConfigureAwait(false))
                status?.Report($"yt-dlp {latest} installiert.");
        }

        private async Task<string> ReadYtDlpVersionAsync(CancellationToken cancellationToken)
        {
            try
            {
                var start = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = YtDlpPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                start.ArgumentList.Add("--version");

                using var process = System.Diagnostics.Process.Start(start);
                if (process == null) return null;

                Task<string> output = process.StandardOutput.ReadToEndAsync();

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                return (await output.ConfigureAwait(false) ?? string.Empty).Trim();
            }
            catch (Exception ex)
            {
                Log.Debug("could not read the yt-dlp version: " + ex.Message);
                return null;
            }
        }

        private static async Task<string> ReadLatestYtDlpTagAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MidnightRadio/1.0");

                string json = await client
                    .GetStringAsync("https://api.github.com/repos/yt-dlp/yt-dlp/releases/latest",
                        cancellationToken)
                    .ConfigureAwait(false);

                // One field is wanted from a large document; a substring read avoids
                // deserialising the whole release payload.
                const string key = "\"tag_name\":";
                int at = json.IndexOf(key, StringComparison.Ordinal);
                if (at < 0) return null;

                int open = json.IndexOf('"', at + key.Length);
                int close = open < 0 ? -1 : json.IndexOf('"', open + 1);
                return open < 0 || close < 0 ? null : json.Substring(open + 1, close - open - 1);
            }
            catch (Exception ex)
            {
                Log.Debug("could not read the latest yt-dlp tag: " + ex.Message);
                return null;
            }
        }

        private static bool OlderThan(string path, TimeSpan age)
        {
            try { return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > age; }
            catch { return false; }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>One download at a time, so two tracks queued at once cannot race.</summary>
        private async Task<string> Serialise(Func<Task<string>> work)
        {
            await _oneAtATime.WaitAsync().ConfigureAwait(false);
            try { return await work().ConfigureAwait(false); }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                Log.Warn("tool download failed: " + ex.Message);
                return null;
            }
            finally { _oneAtATime.Release(); }
        }

        private static async Task<bool> DownloadToFileAsync(
            string url, string destination, IProgress<string> status, CancellationToken cancellationToken)
        {
            // Written to a temporary name first, so an interrupted download cannot leave
            // behind a truncated file that later looks like a working tool.
            string temporary = destination + ".part";

            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("MidnightRadio/1.0");

                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Log.Warn($"download of {url} returned HTTP {(int)response.StatusCode}");
                    return false;
                }

                long? total = response.Content.Headers.ContentLength;

                using (var source = await response.Content.ReadAsStreamAsync(cancellationToken)
                           .ConfigureAwait(false))
                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                           FileShare.None, 64 * 1024, useAsync: true))
                {
                    var buffer = new byte[64 * 1024];
                    long written = 0;
                    int lastReported = -1;

                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                        if (read <= 0) break;

                        await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        written += read;

                        if (total.HasValue && total.Value > 0)
                        {
                            int percent = (int)(written * 100 / total.Value);
                            if (percent != lastReported && percent % 5 == 0)
                            {
                                lastReported = percent;
                                status?.Report($"Download {percent} % …");
                            }
                        }
                    }
                }

                if (new FileInfo(temporary).Length == 0)
                {
                    Log.Warn($"download of {url} produced an empty file");
                    return false;
                }

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Log.Warn($"downloading {url} failed: {ex.Message}");
                return false;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}

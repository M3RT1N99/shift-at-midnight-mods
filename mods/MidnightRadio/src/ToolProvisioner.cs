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

                        if (total is > 0)
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

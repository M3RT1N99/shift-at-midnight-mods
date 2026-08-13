using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MidnightRadio
{
    /// <summary>
    /// Converts audio Unity refuses to decode into something it will play.
    ///
    /// Unity decodes Ogg Vorbis and WAV reliably in a Windows standalone build, but MP3 is
    /// restricted - and a music folder is overwhelmingly MP3 in practice. Rather than tell
    /// people to convert their library by hand, this transcodes on demand with the ffmpeg
    /// that the URL feature already depends on.
    ///
    /// Results are cached by content identity, so a track converts once and every later
    /// play is a straight file read. Nothing here ever throws: if ffmpeg is missing or the
    /// conversion fails, the caller falls back to the original file and the existing
    /// "convert this file" error surfaces as before.
    /// </summary>
    internal sealed class AudioTranscoder
    {
        private readonly ToolLocator _tools;
        private readonly string _cacheDirectory;

        public AudioTranscoder(ToolLocator tools, string dataDirectory)
        {
            _tools = tools;
            _cacheDirectory = Path.Combine(dataDirectory, "Cache", "converted");
        }

        /// <summary>Formats Unity loads directly, so they never need touching.</summary>
        public static bool IsNativelyPlayable(string path)
        {
            string extension = Path.GetExtension(path ?? string.Empty).ToLowerInvariant();
            return extension is ".ogg" or ".oga" or ".wav";
        }

        public string CachedPathFor(TrackInfo track)
        {
            string key = string.IsNullOrEmpty(track?.Id)
                ? Path.GetFileNameWithoutExtension(track?.Path ?? "track")
                : track.Id;

            foreach (char invalid in Path.GetInvalidFileNameChars()) key = key.Replace(invalid, '_');
            if (key.Length > 64) key = key.Substring(0, 64);

            return Path.Combine(_cacheDirectory, key + ".ogg");
        }

        /// <summary>
        /// Returns a path Unity can load: the original when it is already playable, the
        /// cached conversion when one exists, otherwise a freshly converted file. Returns
        /// the original path unchanged if conversion is not possible, so the caller's normal
        /// error handling still applies.
        /// </summary>
        public async Task<string> EnsurePlayableAsync(
            TrackInfo track, IProgress<string> status, CancellationToken cancellationToken)
        {
            if (track == null || string.IsNullOrEmpty(track.Path)) return track?.Path;
            if (IsNativelyPlayable(track.Path)) return track.Path;

            string cached = CachedPathFor(track);
            if (File.Exists(cached) && new FileInfo(cached).Length > 0) return cached;

            ToolResolution ffmpeg;
            try
            {
                ffmpeg = await _tools.FindFfmpegAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("could not look for ffmpeg: " + ex.Message);
                return track.Path;
            }

            if (ffmpeg == null || !ffmpeg.Found || string.IsNullOrEmpty(ffmpeg.ExecutablePath))
            {
                Log.Warn($"'{track.Title}' is {Path.GetExtension(track.Path)}, which Unity will "
                         + "not decode, and ffmpeg was not found - install ffmpeg to play it");
                status?.Report("ffmpeg fehlt - MP3 kann nicht umgewandelt werden.");
                return track.Path;
            }

            status?.Report($"Wandle '{track.Title}' um …");
            return await ConvertAsync(ffmpeg.ExecutablePath, track, cached, cancellationToken)
                       .ConfigureAwait(false)
                   ?? track.Path;
        }

        private static async Task<string> ConvertAsync(
            string ffmpegPath, TrackInfo track, string destination, CancellationToken cancellationToken)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination));

                // Written to a temporary name first so an interrupted run cannot leave a
                // half-file behind that later looks like a valid cache entry.
                string temporary = destination + ".part";
                if (File.Exists(temporary)) File.Delete(temporary);

                var start = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                // -vn drops any embedded cover art, which would otherwise make ffmpeg try to
                // write a video stream into an audio container and fail.
                foreach (string argument in new[]
                         {
                             "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                             "-i", track.Path,
                             "-vn", "-c:a", "libvorbis", "-q:a", "5",
                             "-f", "ogg", temporary,
                         })
                    start.ArgumentList.Add(argument);

                using var process = new Process { StartInfo = start };
                if (!process.Start())
                {
                    Log.Warn("ffmpeg could not be started");
                    return null;
                }

                Task<string> errorText = process.StandardError.ReadToEndAsync();

                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        Log.Warn($"converting '{track.Title}' timed out or was cancelled");
                        TryDelete(temporary);
                        return null;
                    }
                }

                if (process.ExitCode != 0)
                {
                    string message = (await errorText.ConfigureAwait(false) ?? string.Empty).Trim();
                    if (message.Length > 200) message = message.Substring(0, 200);
                    Log.Warn($"ffmpeg failed on '{track.Title}': {message}");
                    TryDelete(temporary);
                    return null;
                }

                if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                {
                    Log.Warn($"ffmpeg produced nothing for '{track.Title}'");
                    TryDelete(temporary);
                    return null;
                }

                if (File.Exists(destination)) File.Delete(destination);
                File.Move(temporary, destination);

                Log.Info($"converted '{track.Title}' to ogg");
                return destination;
            }
            catch (Exception ex)
            {
                Log.Warn($"converting '{track.Title}' failed: {ex.Message}");
                return null;
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}

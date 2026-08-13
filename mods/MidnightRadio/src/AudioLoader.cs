using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace MidnightRadio
{
    /// <summary>
    /// Turns a file on disk into an AudioClip.
    ///
    /// Design note on why this uses UnityWebRequestMultimedia rather than a PCM callback:
    /// AudioClip.Create with a PCMReaderCallback needs a managed delegate to survive being
    /// marshalled into the native audio thread. Under Il2CppInterop that is exactly the
    /// class of thing that breaks - the delegate has to stay alive and reachable across a
    /// boundary the GC does not track, and it is called from a non-Unity thread. Feeding a
    /// file:// URL to Unity's own loader keeps all of that inside native code, where it
    /// already works.
    ///
    /// Format support is the other reason. Unity decodes Ogg Vorbis and WAV reliably on
    /// Windows standalone; MP3 is the historically restricted one. Rather than gamble, the
    /// loader tries the honest AudioType for the extension and reports a clear failure that
    /// the UI can turn into "convert this file" instead of silence.
    /// </summary>
    internal static class AudioLoader
    {
        public sealed class Result
        {
            public AudioClip Clip;
            public string    Error;
            public bool      Ok => Clip != null && string.IsNullOrEmpty(Error);
        }

        /// <summary>
        /// Main-thread cancellation boundary for the native UnityWebRequest. Cancelling
        /// aborts and disposes the request immediately, even if MelonLoader never resumes
        /// the owning coroutine during teardown.
        /// </summary>
        public sealed class LoadTicket
        {
            private UnityWebRequest _request;

            public bool IsCancellationRequested { get; private set; }

            internal bool Attach(UnityWebRequest request)
            {
                if (IsCancellationRequested) return false;
                _request = request;
                return true;
            }

            internal void Complete(UnityWebRequest request)
            {
                if (ReferenceEquals(_request, request)) _request = null;
            }

            public void Cancel()
            {
                if (IsCancellationRequested) return;
                IsCancellationRequested = true;
                UnityWebRequest request = _request;
                _request = null;
                if (request == null) return;
                try { request.Abort(); } catch { }
                try { request.Dispose(); } catch { }
            }
        }

        public static AudioType GuessType(string path)
        {
            switch (Path.GetExtension(path ?? string.Empty).ToLowerInvariant())
            {
                case ".ogg":
                case ".oga": return AudioType.OGGVORBIS;
                case ".wav": return AudioType.WAV;
                case ".mp3": return AudioType.MPEG;
                case ".aiff":
                case ".aif": return AudioType.AIFF;
                default:     return AudioType.UNKNOWN;
            }
        }

        public static bool IsSupportedExtension(string path) => GuessType(path) != AudioType.UNKNOWN;

        /// <summary>
        /// Loads a clip. Yields until done; inspect <paramref name="result"/> afterwards.
        /// Streaming is on, so a long track starts quickly and does not sit in memory whole.
        /// </summary>
        public static IEnumerator Load(string filePath, Result result, LoadTicket ticket = null)
        {
            if (result == null) yield break;

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                result.Error = "file not found";
                yield break;
            }

            var type = GuessType(filePath);
            if (type == AudioType.UNKNOWN)
            {
                result.Error = "unsupported format - convert to .ogg or .wav";
                yield break;
            }

            string url = new Uri(Path.GetFullPath(filePath)).AbsoluteUri;

            UnityWebRequest req = null;
            try
            {
                req = UnityWebRequestMultimedia.GetAudioClip(url, type);
                if (ticket != null && !ticket.Attach(req))
                {
                    result.Error = "load cancelled";
                    req.Dispose();
                    yield break;
                }
                var handler = req.downloadHandler as DownloadHandlerAudioClip;
                if (handler != null)
                {
                    handler.streamAudio = true;
                    handler.compressed  = false;
                }
            }
            catch (Exception ex)
            {
                result.Error = "request setup failed: " + ex.Message;
                ticket?.Complete(req);
                if (req != null) req.Dispose();
                yield break;
            }

            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = req.SendWebRequest();
            }
            catch (Exception ex)
            {
                result.Error = "request start failed: " + ex.Message;
                ticket?.Complete(req);
                req.Dispose();
                yield break;
            }

            while (true)
            {
                if (ticket?.IsCancellationRequested == true)
                {
                    result.Error = "load cancelled";
                    yield break;
                }

                bool complete;
                try { complete = operation.isDone; }
                catch (Exception ex)
                {
                    result.Error = "request failed: " + ex.Message;
                    ticket?.Complete(req);
                    req.Dispose();
                    yield break;
                }
                if (complete) break;
                yield return null;
            }

            // The request itself is not wrapped in try/catch because C# forbids yielding
            // inside one; everything that can throw is isolated above and below instead.
            if (req.result != UnityWebRequest.Result.Success)
            {
                result.Error = DescribeFailure(req, type);
                ticket?.Complete(req);
                req.Dispose();
                yield break;
            }

            try
            {
                var clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null)
                {
                    result.Error = "decoder returned no clip";
                }
                else
                {
                    clip.name = Path.GetFileNameWithoutExtension(filePath);
                    result.Clip = clip;
                }
            }
            catch (Exception ex)
            {
                result.Error = "decode failed: " + ex.Message;
            }
            finally
            {
                ticket?.Complete(req);
                req.Dispose();
            }
        }

        private static string DescribeFailure(UnityWebRequest req, AudioType type)
        {
            // MP3 is the format most likely to fail on a standalone build, so name the fix
            // rather than echoing an opaque Unity error.
            if (type == AudioType.MPEG)
                return $"could not decode MP3 ({req.error}) - convert it to .ogg or .wav";

            return string.IsNullOrEmpty(req.error) ? "load failed" : req.error;
        }

        /// <summary>Releases a clip we created. Safe to call with null.</summary>
        public static void Unload(AudioClip clip)
        {
            if (clip == null) return;
            Log.Guard("AudioLoader.Unload", () =>
            {
                clip.UnloadAudioData();
                UnityEngine.Object.Destroy(clip);
            });
        }
    }
}

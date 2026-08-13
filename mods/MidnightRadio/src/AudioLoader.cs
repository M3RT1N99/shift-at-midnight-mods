using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>
    /// Builds an AudioClip from raw PCM produced by ffmpeg.
    ///
    /// Two Unity routes are unavailable on this IL2CPP build, both confirmed at runtime:
    ///
    ///  - UnityWebRequestMultimedia.GetAudioClip throws
    ///    "Method not found: DownloadHandlerAudioClip..ctor(String, AudioType)". The game
    ///    never uses it, so the linker removed it.
    ///  - AudioClip.Create's plain overload is gone as well. Only the PCMReaderCallback
    ///    overloads remain, and they accept a null callback - which is exactly what is done
    ///    here, so no managed delegate is ever marshalled into the native audio thread.
    ///
    /// What does survive is AudioClip.SetData(float[], int), so the samples are decoded
    /// out-of-process by ffmpeg into interleaved 32-bit float and pushed straight in.
    ///
    /// The file is read in chunks across frames. A four-minute stereo track is about 84 MB
    /// of float data, and reading that in one go would stall the frame the panel is on.
    /// </summary>
    internal static class AudioLoader
    {
        /// <summary>Samples per channel handed over per frame while reading.</summary>
        private const int ChunkFrames = 256 * 1024;

        public sealed class Result
        {
            public AudioClip Clip;
            public string Error;
            public bool Ok => Clip != null && string.IsNullOrEmpty(Error);
        }

        /// <summary>
        /// Cancellation boundary. The load spans several frames, so a scene change or a new
        /// track has to be able to stop it part way.
        /// </summary>
        public sealed class LoadTicket
        {
            public bool IsCancellationRequested { get; private set; }
            public void Cancel() => IsCancellationRequested = true;
        }

        /// <summary>
        /// Reads a raw f32le PCM file, as produced by AudioTranscoder, into an AudioClip.
        /// Yields between chunks; inspect <paramref name="result"/> when it completes.
        /// </summary>
        public static IEnumerator Load(string pcmPath, Result result, LoadTicket ticket = null)
        {
            if (result == null) yield break;

            if (string.IsNullOrWhiteSpace(pcmPath) || !File.Exists(pcmPath))
            {
                result.Error = "decoded audio not found";
                yield break;
            }

            long byteLength;
            try { byteLength = new FileInfo(pcmPath).Length; }
            catch (Exception ex)
            {
                result.Error = "could not read the decoded audio: " + ex.Message;
                yield break;
            }

            const int channels = AudioTranscoder.Channels;
            const int frequency = AudioTranscoder.SampleRate;

            int totalSamples = (int)(byteLength / sizeof(float));
            int framesPerChannel = totalSamples / channels;
            if (framesPerChannel <= 0)
            {
                result.Error = "decoded audio is empty";
                yield break;
            }

            AudioClip clip = null;
            FileStream stream = null;

            try
            {
                // The null callback is deliberate: this overload is the only one left, and
                // passing null keeps every sample transfer on the managed side via SetData.
                clip = AudioClip.Create(
                    Path.GetFileNameWithoutExtension(pcmPath),
                    framesPerChannel, channels, frequency, false, null);

                if (clip == null)
                {
                    result.Error = "AudioClip.Create returned nothing";
                    yield break;
                }

                stream = new FileStream(pcmPath, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            }
            catch (Exception ex)
            {
                result.Error = "could not prepare playback: " + ex.Message;
                stream?.Dispose();
                if (clip != null) UnityEngine.Object.Destroy(clip);
                yield break;
            }

            var chunk = new float[ChunkFrames * channels];
            var bytes = new byte[chunk.Length * sizeof(float)];
            int writtenFrames = 0;
            string failure = null;

            while (writtenFrames < framesPerChannel)
            {
                if (ticket != null && ticket.IsCancellationRequested)
                {
                    failure = "load cancelled";
                    break;
                }

                int read;
                try { read = stream.Read(bytes, 0, bytes.Length); }
                catch (Exception ex) { failure = "read failed: " + ex.Message; break; }
                if (read <= 0) break;

                int samples = read / sizeof(float);
                int frames = samples / channels;
                if (frames <= 0) break;

                try
                {
                    Buffer.BlockCopy(bytes, 0, chunk, 0, frames * channels * sizeof(float));

                    // The final chunk is usually short, and SetData expects an array sized
                    // to what is being written.
                    if (frames == ChunkFrames)
                    {
                        clip.SetData(chunk, writtenFrames);
                    }
                    else
                    {
                        var tail = new float[frames * channels];
                        Array.Copy(chunk, tail, tail.Length);
                        clip.SetData(tail, writtenFrames);
                    }
                }
                catch (Exception ex) { failure = "SetData failed: " + ex.Message; break; }

                writtenFrames += frames;
                yield return null;
            }

            stream.Dispose();

            if (failure != null)
            {
                result.Error = failure;
                if (clip != null) UnityEngine.Object.Destroy(clip);
                yield break;
            }

            result.Clip = clip;
        }

        /// <summary>Releases a clip we created. Safe to call with null.</summary>
        public static void Unload(AudioClip clip)
        {
            if (clip == null) return;
            Log.Guard("AudioLoader.Unload", () => UnityEngine.Object.Destroy(clip));
        }
    }
}

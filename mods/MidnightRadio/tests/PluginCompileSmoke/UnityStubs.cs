using System;
using System.Collections;

namespace UnityEngine
{
    public class Object
    {
        public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) => Array.Empty<T>();
        public static void Destroy(Object value) { }
    }

    public class Component : Object
    {
        public GameObject gameObject { get; set; } = new();
        public Transform transform => gameObject.transform;
        public bool enabled { get; set; } = true;
    }

    public class GameObject : Object
    {
        public GameObject() { transform = new Transform { gameObject = this }; }
        public GameObject(string value) : this() { name = value; }
        public string name { get; set; } = string.Empty;
        public bool activeInHierarchy { get; set; } = true;
        public SceneManagement.Scene scene { get; set; } = new SceneManagement.Scene(true, true);
        public Transform transform { get; }
        public T AddComponent<T>() where T : new() => new();

        // The stub carries no component graph, so this returns nothing. That is enough for
        // the compile smoke test; the real behaviour is exercised in-game.
        public T[] GetComponentsInChildren<T>(bool includeInactive = false) => Array.Empty<T>();
    }

    public class Transform : Object
    {
        public Transform parent { get; set; }
        public GameObject gameObject { get; set; }
        public Vector3 localPosition { get; set; }
        public void SetParent(Transform value, bool worldPositionStays) => parent = value;
        public Transform Find(string value) => null;
        public T GetComponent<T>() where T : class => null;
    }

    public struct Vector3 { public static Vector3 zero => default; }
    public struct Vector2 { }
    public struct Rect
    {
        public Rect(float x, float y, float width, float height)
        { this.x = x; this.y = y; this.width = width; this.height = height; }
        public float x, y, width, height;
    }

    public enum FindObjectsInactive { Exclude, Include }
    public enum FindObjectsSortMode { None }
    public enum AudioType { UNKNOWN, ACC, AIFF, MPEG, OGGVORBIS, WAV }
    public enum AudioRolloffMode { Logarithmic, Linear, Custom }
    public enum CursorLockMode { None, Locked, Confined }
    public enum KeyCode { None, F4, Escape }

    public class AudioClip : Object
    {
        public string name { get; set; }
        public float length { get; set; }
        public bool UnloadAudioData() => true;

        // Mirrors the only overload that survives stripping in the shipped game: the one
        // taking a PCMReaderCallback, which the mod passes as null.
        public delegate void PCMReaderCallback(float[] data);
        public static AudioClip Create(
            string name, int lengthSamples, int channels, int frequency, bool stream,
            PCMReaderCallback pcmreadercallback)
            => new() { name = name, length = frequency > 0 ? lengthSamples / (float)frequency : 0f };

        public bool SetData(float[] data, int offsetSamples) => true;
    }

    public class AudioSource : Component
    {
        public Audio.AudioMixerGroup outputAudioMixerGroup { get; set; }
        public float spatialBlend { get; set; }
        public AudioRolloffMode rolloffMode { get; set; }
        public float minDistance { get; set; }
        public float maxDistance { get; set; }
        public float dopplerLevel { get; set; }
        public float spread { get; set; }
        public int priority { get; set; }
        public bool playOnAwake { get; set; }
        public bool loop { get; set; }
        public float volume { get; set; }
        public bool mute { get; set; }
        public AudioClip clip { get; set; }
        public float pitch { get; set; }
        public float time { get; set; }
        public bool isPlaying { get; set; }
        public void Play() { isPlaying = true; }
        public void Stop() { isPlaying = false; }
        public void Pause() { isPlaying = false; }
        public void UnPause() { isPlaying = true; }
    }

    public static class Mathf
    {
        public static bool Approximately(float a, float b) => Math.Abs(a - b) < 0.0001f;
        public static float Max(float a, float b) => Math.Max(a, b);
        public static float Abs(float value) => Math.Abs(value);
        public static float Clamp(float value, float min, float max) => Math.Min(Math.Max(value, min), max);
        public static float Clamp01(float value) => Clamp(value, 0f, 1f);
        public static float Sign(float value) => Math.Sign(value);
        public static int Max(int a, int b) => Math.Max(a, b);
        // Unity rounds halves away from zero, unlike .NET's banker's rounding default.
        public static int RoundToInt(float value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }

    public static class Time
    {
        public static float realtimeSinceStartup { get; set; }
        public static float unscaledDeltaTime { get; set; }
    }

    public static class Input { public static bool GetKeyDown(KeyCode key) => false; }

    // Settable so a test can pin a resolution and check the panel centres inside it.
    public static class Screen
    {
        public static int width { get; set; } = 1920;
        public static int height { get; set; } = 1080;
    }
    public static class Cursor
    {
        public static bool visible { get; set; }
        public static CursorLockMode lockState { get; set; }
    }

    public sealed class GUISkin { public GUIStyle box { get; } = new(); }
    public sealed class GUIStyle { }
    public sealed class GUILayoutOption { }
    public static class GUI
    {
        public sealed class WindowFunction
        {
            private readonly Action<int> _callback;
            private WindowFunction(Action<int> callback) { _callback = callback; }
            public static implicit operator WindowFunction(Action<int> callback) => new(callback);
            public void Invoke(int id) => _callback(id);
        }

        public static bool enabled { get; set; } = true;
        public static GUISkin skin { get; } = new();
        public static Rect Window(int id, Rect rect, WindowFunction draw, string title) { draw.Invoke(id); return rect; }
        public static void DragWindow(Rect rect) { }
    }
    public static class GUIUtility { public static string systemCopyBuffer { get; set; } }
    public static class GUILayout
    {
        public static void BeginVertical(params GUILayoutOption[] options) { }
        public static void BeginVertical(GUIStyle style, params GUILayoutOption[] options) { }
        public static void EndVertical() { }
        public static void BeginHorizontal(params GUILayoutOption[] options) { }
        public static void EndHorizontal() { }
        public static void Label(string value, params GUILayoutOption[] options) { }
        public static string TextField(string value, params GUILayoutOption[] options) => value;
        public static bool Button(string value, params GUILayoutOption[] options) => false;
        public static bool Toggle(bool value, string text, params GUILayoutOption[] options) => value;
        public static float HorizontalSlider(float value, float left, float right, params GUILayoutOption[] options) => value;
        public static Vector2 BeginScrollView(Vector2 value, params GUILayoutOption[] options) => value;
        public static Vector2 BeginScrollView(Vector2 value, GUIStyle style, params GUILayoutOption[] options) => value;
        public static void EndScrollView() { }
        public static void Space(float pixels) { }
        public static GUILayoutOption Width(float value) => new();
    }
}

namespace UnityEngine.SceneManagement
{
    public readonly struct Scene
    {
        private readonly bool _valid;
        public Scene(bool valid, bool loaded) { _valid = valid; isLoaded = loaded; }
        public bool isLoaded { get; }
        public bool IsValid() => _valid;
    }
}

namespace UnityEngine.Audio
{
    public class AudioMixerGroup : UnityEngine.Object { }
}

namespace UnityEngine.Networking
{
    public class DownloadHandler : IDisposable { public void Dispose() { } }
    public class DownloadHandlerAudioClip : DownloadHandler
    {
        public bool streamAudio { get; set; }
        public bool compressed { get; set; }
        public static UnityEngine.AudioClip GetContent(UnityWebRequest request) => new();
    }
    public class UnityWebRequestAsyncOperation { public bool isDone { get; set; } = true; }
    public class UnityWebRequest : IDisposable
    {
        public enum Result { InProgress, Success, ConnectionError, ProtocolError, DataProcessingError }
        public Result result { get; set; }
        public string error { get; set; }
        public DownloadHandler downloadHandler { get; set; }
        public UnityWebRequestAsyncOperation SendWebRequest() => new();
        public void Dispose() { }
        public void Abort() { }
    }
    public static class UnityWebRequestMultimedia
    {
        public static UnityWebRequest GetAudioClip(string uri, UnityEngine.AudioType type) =>
            new() { downloadHandler = new DownloadHandlerAudioClip() };
    }
}

namespace UnityEngine.InputSystem
{
    // Enough of PlayerInput for InputLock to compile. The stub owns no input, so the
    // suspend/restore behaviour is exercised in-game rather than here.
    public class PlayerInput : Object
    {
        public bool inputIsActive { get; private set; } = true;
        public void DeactivateInput() => inputIsActive = false;
        public void ActivateInput() => inputIsActive = true;
    }
}

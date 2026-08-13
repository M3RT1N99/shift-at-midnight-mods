using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>
    /// Intentionally small IMGUI control surface. It does not inject an EventSystem or
    /// touch the game's input action maps, which keeps the first playable slice robust on
    /// IL2CPP. F4 toggles it; closing it immediately releases the cursor.
    /// </summary>
    internal sealed class RadioUI
    {
        private readonly Config _config;
        private readonly MusicLibrary _library;
        private readonly PlaybackController _player;
        private readonly Action<TrackInfo> _play;
        private readonly Action _reload;
        private readonly Action _next;
        private readonly Action _previous;
        private readonly Action<string> _downloadUrl;
        private readonly Action _saveConfig;
        private readonly GUI.WindowFunction _drawWindow;

        private bool _open;
        private Rect _window = new(50f, 50f, 760f, 520f);
        private Vector2 _scroll;
        private string _search = string.Empty;
        private string _url = string.Empty;
        private string _status = string.Empty;
        private bool _showUrlNotice;
        private bool _urlNoticeChecked;
        private bool _cursorWasVisible;
        private CursorLockMode _cursorWasLocked;

        public RadioUI(
            Config config,
            MusicLibrary library,
            PlaybackController player,
            Action<TrackInfo> play,
            Action reload,
            Action next,
            Action previous,
            Action<string> downloadUrl,
            Action saveConfig)
        {
            _config = config;
            _library = library;
            _player = player;
            _play = play;
            _reload = reload;
            _next = next;
            _previous = previous;
            _downloadUrl = downloadUrl;
            _saveConfig = saveConfig;
            // The generated IL2CPP wrapper exposes a conversion from System.Action<int>
            // rather than accepting a managed method group directly. Keep the converted
            // delegate rooted for the full lifetime of the panel.
            _drawWindow = (Action<int>)DrawWindow;
        }

        public bool IsOpen => _open;

        public void Tick()
        {
            if (HotkeyPressed(_config.Hotkey))
            {
                if (_open) Close();
                else Open();
                return;
            }

            if (_open && HotkeyPressed("Escape"))
                Close();

            // The game may re-lock the cursor from its own Update callback. Reassert the
            // panel's cursor state for as long as it is visible, then restore the exact
            // previous state in Close().
            if (_open)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
            }
        }

        public void Draw()
        {
            if (!_open) return;
            _window = GUI.Window(0x4D52, _window, _drawWindow, "Midnight Radio");
        }

        public void SetStatus(string status) => _status = status ?? string.Empty;

        public void Close()
        {
            if (!_open) return;
            _open = false;
            Cursor.visible = _cursorWasVisible;
            Cursor.lockState = _cursorWasLocked;
            _saveConfig();
        }

        /// <summary>
        /// Also called when the player interacts with the radio in the world, which is the
        /// primary way in - the hotkey is the fallback.
        /// </summary>
        public void Open()
        {
            _cursorWasVisible = Cursor.visible;
            _cursorWasLocked = Cursor.lockState;
            _open = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void DrawWindow(int id)
        {
            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Suche", GUILayout.Width(50f));
            _search = GUILayout.TextField(_search ?? string.Empty);
            if (GUILayout.Button("Neu laden", GUILayout.Width(100f))) _reload();
            if (GUILayout.Button("Ordner", GUILayout.Width(70f))) OpenMusicDirectory();
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("◀", GUILayout.Width(44f))) _previous();
            if (GUILayout.Button(_player.IsPaused ? "Weiter" : "Pause", GUILayout.Width(75f)))
                _player.TogglePause();
            if (GUILayout.Button("■", GUILayout.Width(44f))) _player.Stop();
            if (GUILayout.Button("▶", GUILayout.Width(44f))) _next();

            bool muted = GUILayout.Toggle(_player.IsMuted, "Stumm", GUILayout.Width(80f));
            if (muted != _player.IsMuted) _player.SetMuted(muted);

            GUILayout.Label($"Lautstärke {_config.Playback.Volume:P0}", GUILayout.Width(120f));
            _config.Playback.Volume = GUILayout.HorizontalSlider(
                _config.Playback.Volume, 0f, _config.Playback.VolumeCeiling,
                GUILayout.Width(140f));
            GUILayout.EndHorizontal();

            var current = _player.CurrentTrack;
            GUILayout.Label(current == null
                ? "Kein Titel geladen"
                : $"Jetzt: {current.Title}  ({FormatTime(_player.PositionSeconds)})");

            if (_player.IsLoading) GUILayout.Label("Lade Audio …");
            if (!string.IsNullOrEmpty(_player.LastError))
                GUILayout.Label("Fehler: " + _player.LastError);
            if (!string.IsNullOrEmpty(_status)) GUILayout.Label(_status);

            _scroll = GUILayout.BeginScrollView(_scroll, GUI.skin.box);
            IReadOnlyList<TrackInfo> tracks = _library.Snapshot;
            string needle = (_search ?? string.Empty).Trim();
            foreach (var track in tracks)
            {
                if (needle.Length > 0 &&
                    track.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("▶", GUILayout.Width(35f))) _play(track);
                GUILayout.Label(track.Title);
                GUILayout.Label(FormatBytes(track.SizeBytes), GUILayout.Width(85f));
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();

            GUILayout.Label(
                "Lokale Titel werden per Inhalts-Hash indexiert. Dieser Entwicklungsstand " +
                "überträgt weder Titelkennungen noch Audiodateien an Mitspieler.");

            GUILayout.Space(8f);
            DrawUrlSection();

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0f, 0f, _window.width, 24f));
        }

        private void DrawUrlSection()
        {
            GUILayout.Label("Link über yt-dlp laden (optional)");

            if (_showUrlNotice)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(
                    "yt-dlp und ffmpeg werden nicht mitgeliefert oder automatisch geladen. " +
                    "Du bist selbst dafür verantwortlich, dass der Abruf und die Nutzung " +
                    "des Inhalts erlaubt sind. Der Link wird nur für deine lokale Wiedergabe " +
                    "geladen; Audiodaten werden nicht zwischen Spielern übertragen.");
                _urlNoticeChecked = GUILayout.Toggle(
                    _urlNoticeChecked, "Ich habe den Hinweis verstanden.");
                GUILayout.BeginHorizontal();
                GUI.enabled = _urlNoticeChecked;
                if (GUILayout.Button("URL-Modus aktivieren"))
                {
                    _config.UrlMode.NoticeAcceptedVersion = 1;
                    _config.UrlMode.AcceptedAt = DateTime.UtcNow.ToString("o");
                    _config.UrlMode.Enabled = true;
                    _showUrlNotice = false;
                    _saveConfig();
                }
                GUI.enabled = true;
                if (GUILayout.Button("Abbrechen")) _showUrlNotice = false;
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                return;
            }

            bool requested = GUILayout.Toggle(_config.UrlMode.Enabled, "URL-Modus aktiviert");
            if (requested != _config.UrlMode.Enabled)
            {
                if (requested && _config.UrlMode.NoticeAcceptedVersion < 1)
                {
                    _showUrlNotice = true;
                    _urlNoticeChecked = false;
                }
                else
                {
                    _config.UrlMode.Enabled = requested;
                    _saveConfig();
                }
            }

            GUI.enabled = _config.UrlMode.Enabled;
            GUILayout.BeginHorizontal();
            _url = GUILayout.TextField(_url ?? string.Empty);
            if (GUILayout.Button("Einfügen", GUILayout.Width(75f)))
                _url = GUIUtility.systemCopyBuffer ?? string.Empty;
            if (GUILayout.Button("Laden", GUILayout.Width(75f)))
            {
                string value = (_url ?? string.Empty).Trim();
                if (value.Length != 0) _downloadUrl(value);
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;
        }

        private void OpenMusicDirectory()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _library.MusicDirectory,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                SetStatus("Ordner konnte nicht geöffnet werden: " + ex.Message);
            }
        }

        private static KeyCode ParseKey(string value)
        {
            return Enum.TryParse(value, ignoreCase: true, out KeyCode key) ? key : KeyCode.F4;
        }

        private static bool HotkeyPressed(string value)
        {
            // Shift At Midnight uses the new Input System. Query it first so F4 still
            // works when legacy input handling is disabled in Player Settings. Reflection
            // keeps this boundary tolerant of minor wrapper/API reshuffles.
            try
            {
                Type keyboardType = Type.GetType(
                    "UnityEngine.InputSystem.Keyboard, Unity.InputSystem",
                    throwOnError: false);
                object keyboard = keyboardType?.GetProperty(
                    "current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (keyboard != null)
                {
                    string keyName = string.IsNullOrWhiteSpace(value) ? "F4" : value.Trim();
                    string propertyName = InputSystemPropertyName(keyName);
                    object key = keyboard.GetType().GetProperty(
                        propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(keyboard);
                    object pressed = key?.GetType().GetProperty(
                        "wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance)?.GetValue(key);
                    if (pressed is bool state) return state;
                }
            }
            catch { }

            try { return Input.GetKeyDown(ParseKey(value)); }
            catch { return false; }
        }

        private static string InputSystemPropertyName(string keyName)
        {
            switch (keyName.ToLowerInvariant())
            {
                case "return": return "enterKey";
                case "leftcontrol": return "leftCtrlKey";
                case "rightcontrol": return "rightCtrlKey";
                case "leftwindows": return "leftMetaKey";
                case "rightwindows": return "rightMetaKey";
                case "escape": return "escapeKey";
                case "space": return "spaceKey";
            }

            if (keyName.Length == 6 && keyName.StartsWith("Alpha", StringComparison.OrdinalIgnoreCase) &&
                char.IsDigit(keyName[5]))
                return "digit" + keyName[5] + "Key";

            return char.ToLowerInvariant(keyName[0]) + keyName.Substring(1) + "Key";
        }

        private static string FormatTime(float seconds)
        {
            if (seconds < 0f || float.IsNaN(seconds)) seconds = 0f;
            var span = TimeSpan.FromSeconds(seconds);
            return span.TotalHours >= 1d
                ? span.ToString(@"h\:mm\:ss")
                : span.ToString(@"m\:ss");
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.0} MB";
            if (bytes >= 1024L) return $"{bytes / 1024d:0.0} KB";
            return bytes + " B";
        }
    }
}

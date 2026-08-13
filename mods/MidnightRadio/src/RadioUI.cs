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
        private bool _placed;
        private float _lastScreenWidth, _lastScreenHeight;
        private const int TracksPerPage = 8;
        private int _page;
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
            if (_open) InputLock.Tick();
        }

        public void Draw()
        {
            if (!_open) return;
            _window = GUI.Window(0x4D52, _window, _drawWindow, "Midnight Radio");
        }

        public void SetStatus(string status) => _status = status ?? string.Empty;

        /// <summary>
        /// Centres the panel the first time it opens, and again if a resolution change has
        /// left it partly off-screen. A position the player dragged to is otherwise kept.
        /// </summary>
        private void CentreIfNeeded()
        {
            float screenWidth = Screen.width;
            float screenHeight = Screen.height;
            if (screenWidth <= 0f || screenHeight <= 0f) return;

            bool offScreen = _window.x < 0f || _window.y < 0f ||
                             _window.x + _window.width > screenWidth ||
                             _window.y + _window.height > screenHeight;
            bool resized = !Mathf.Approximately(_lastScreenWidth, screenWidth) ||
                           !Mathf.Approximately(_lastScreenHeight, screenHeight);
            if (_placed && !offScreen && !resized) return;

            _lastScreenWidth = screenWidth;
            _lastScreenHeight = screenHeight;

            // Sized as a share of the screen rather than in fixed pixels: a 760x520 panel
            // is comfortable at 1080p, a stamp at 4K and off the edge at 720p. The bounds
            // keep it readable on very small screens and stop it swallowing very large ones.
            _window.width = Math.Min(Math.Max(screenWidth * 0.55f, 640f), screenWidth - 40f);
            _window.height = Math.Min(Math.Max(screenHeight * 0.62f, 440f), screenHeight - 40f);
            _window.x = (screenWidth - _window.width) * 0.5f;
            _window.y = (screenHeight - _window.height) * 0.5f;
            _placed = true;
        }

        public void Close()
        {
            if (!_open) return;
            _open = false;
            InputLock.Unlock();
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
            CentreIfNeeded();
            _cursorWasVisible = Cursor.visible;
            _cursorWasLocked = Cursor.lockState;
            InputLock.Lock();
            _open = true;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        private void DrawWindow(int id)
        {
            

            Ui.BeginRow();
            Ui.Label("Suche", 50f);
            _search = Ui.TextField(_search ?? string.Empty);
            if (Ui.Button("Neu laden", 100f)) _reload();
            if (Ui.Button("Ordner", 70f)) OpenMusicDirectory();
            Ui.EndRow();

            Ui.BeginRow();
            if (Ui.Button("◀", 44f)) _previous();
            if (Ui.Button(_player.IsPaused ? "Weiter" : "Pause", 75f))
                _player.TogglePause();
            if (Ui.Button("■", 44f)) _player.Stop();
            if (Ui.Button("▶", 44f)) _next();

            bool muted = Ui.Toggle(_player.IsMuted, "Stumm", 80f);
            if (muted != _player.IsMuted) _player.SetMuted(muted);

            Ui.Label($"Lautstärke {_config.Playback.Volume:P0}", 120f);
            _config.Playback.Volume = Ui.Slider(
                _config.Playback.Volume, 0f, _config.Playback.VolumeCeiling);
            Ui.EndRow();

            var current = _player.CurrentTrack;
            Ui.Label(current == null
                ? "Kein Titel geladen"
                : $"Jetzt: {current.Title}  ({FormatTime(_player.PositionSeconds)})");

            if (_player.IsLoading) Ui.Label("Lade Audio …");
            if (!string.IsNullOrEmpty(_player.LastError))
                Ui.Label("Fehler: " + _player.LastError);
            if (!string.IsNullOrEmpty(_status)) Ui.Label(_status);

            // Paged, not scrolled. GUILayout.BeginScrollView is stripped from this IL2CPP
            // build - the game uses no IMGUI scroll views of its own - and Il2CppInterop
            // cannot unstrip it, so calling it throws "Method unstripping failed" on every
            // OnGUI frame. Paging needs only Label, Button and BeginHorizontal, all of
            // which are present because the game itself uses them.
            IReadOnlyList<TrackInfo> tracks = _library.Snapshot;
            string needle = (_search ?? string.Empty).Trim();

            var visible = new List<TrackInfo>();
            foreach (var track in tracks)
            {
                if (needle.Length > 0 &&
                    track.Title.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                visible.Add(track);
            }

            int pageCount = Math.Max(1, (visible.Count + TracksPerPage - 1) / TracksPerPage);
            _page = Math.Min(Math.Max(_page, 0), pageCount - 1);
            int start = _page * TracksPerPage;

            for (int i = start; i < Math.Min(start + TracksPerPage, visible.Count); i++)
            {
                var track = visible[i];
                Ui.BeginRow();
                if (Ui.Button("▶", 35f)) _play(track);
                Ui.Label(track.Title);
                Ui.Label(FormatBytes(track.SizeBytes), 85f);
                Ui.EndRow();
            }

            if (visible.Count == 0)
                Ui.Label(tracks.Count == 0
                    ? "Keine Musik gefunden. Lege .ogg- oder .wav-Dateien in den Music-Ordner "
                      + "und klicke auf „Neu einlesen“."
                    : "Kein Titel passt zur Suche.");

            if (pageCount > 1)
            {
                Ui.BeginRow();
                if (Ui.Button("◀", 35f) && _page > 0) _page--;
                Ui.Label($"Seite {_page + 1} / {pageCount}   ({visible.Count} Titel)");
                if (Ui.Button("▶", 35f) && _page < pageCount - 1) _page++;
                Ui.EndRow();
            }

            Ui.Space(8f);
            DrawUrlSection();

            
            GUI.DragWindow(new Rect(0f, 0f, _window.width, 24f));
        }

        private void DrawUrlSection()
        {
            Ui.Label("Link über yt-dlp laden (optional)");

            if (_showUrlNotice)
            {
                Ui.BeginRow(); Ui.EndRow();
                Ui.Label(
                    "yt-dlp und ffmpeg werden nicht mitgeliefert oder automatisch geladen. " +
                    "Du bist selbst dafür verantwortlich, dass der Abruf und die Nutzung " +
                    "des Inhalts erlaubt sind. Der Link wird nur für deine lokale Wiedergabe " +
                    "geladen; Audiodaten werden nicht zwischen Spielern übertragen.");
                _urlNoticeChecked = Ui.Toggle(
                    _urlNoticeChecked, "Ich habe den Hinweis verstanden.");
                Ui.BeginRow();
                GUI.enabled = _urlNoticeChecked;
                if (Ui.Button("URL-Modus aktivieren"))
                {
                    _config.UrlMode.NoticeAcceptedVersion = 1;
                    _config.UrlMode.AcceptedAt = DateTime.UtcNow.ToString("o");
                    _config.UrlMode.Enabled = true;
                    _showUrlNotice = false;
                    _saveConfig();
                }
                GUI.enabled = true;
                if (Ui.Button("Abbrechen")) _showUrlNotice = false;
                Ui.EndRow();
                
                return;
            }

            bool requested = Ui.Toggle(_config.UrlMode.Enabled, "URL-Modus aktiviert");
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
            Ui.BeginRow();
            _url = Ui.TextField(_url ?? string.Empty);
            if (Ui.Button("Einfügen", 75f))
                _url = GUIUtility.systemCopyBuffer ?? string.Empty;
            if (Ui.Button("Laden", 75f))
            {
                string value = (_url ?? string.Empty).Trim();
                if (value.Length != 0) _downloadUrl(value);
            }
            Ui.EndRow();
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

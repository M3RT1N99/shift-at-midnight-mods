using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>
    /// A thin, self-healing layer over IMGUI.
    ///
    /// Most of UnityEngine.GUILayout is stripped from this IL2CPP build - the game draws its
    /// own UI with uGUI and UI Toolkit, so the immediate-mode helpers were never referenced
    /// and the linker removed them. Calling one throws
    /// "System.NotSupportedException: Method unstripping failed" on EVERY OnGUI frame, which
    /// is how a single widget produced 11,000 log entries in one session.
    ///
    /// Rather than guess which calls survived, each risky widget is tried once. If it throws,
    /// the failure is logged with the method name, the widget is marked unavailable, and from
    /// then on a fallback built only from Label, Button and BeginHorizontal is used - those
    /// are known good, because the panel rendered fine until it reached the first stripped
    /// call.
    ///
    /// Everything is also scaled from the screen resolution, so the panel keeps its
    /// proportions from 1280x720 up to 4K instead of becoming a stamp in the corner.
    /// </summary>
    internal static class Ui
    {
        // The layout was authored against 1080p; everything else is a ratio of it.
        private const float ReferenceHeight = 1080f;

        private static readonly HashSet<string> Unavailable = new();

        /// <summary>Scale factor for the current resolution, clamped to stay legible and sane.</summary>
        public static float Scale
        {
            get
            {
                float height = Screen.height;
                if (height <= 0f) return 1f;
                return Math.Min(Math.Max(height / ReferenceHeight, 0.75f), 2.5f);
            }
        }

        /// <summary>
        /// Widths are NOT multiplied here. RadioUI.Draw scales the whole GUI matrix, so
        /// everything inside already works in virtual pixels authored at 1080p - scaling
        /// them again would apply the factor twice.
        /// </summary>
        public static float Px(float atReference) => atReference;

        public static GUILayoutOption W(float atReference) => GUILayout.Width(atReference);

        private static bool Available(string name) => !Unavailable.Contains(name);

        private static void MarkUnavailable(string name, Exception ex)
        {
            if (!Unavailable.Add(name)) return;
            Log.Warn($"IMGUI '{name}' is stripped from this build ({ex.GetType().Name}); "
                     + "using a fallback for the rest of the session");
        }

        // ------------------------------------------------------------------ widgets

        public static void Label(string text)
        {
            try { GUILayout.Label(text); }
            catch (Exception ex) { MarkUnavailable("Label", ex); }
        }

        public static void Label(string text, float width)
        {
            try { GUILayout.Label(text, W(width)); }
            catch (Exception ex) { MarkUnavailable("Label", ex); }
        }

        public static bool Button(string text, float width = 0f)
        {
            try
            {
                return width > 0f ? GUILayout.Button(text, W(width)) : GUILayout.Button(text);
            }
            catch (Exception ex)
            {
                MarkUnavailable("Button", ex);
                return false;
            }
        }

        public static void BeginRow()
        {
            try { GUILayout.BeginHorizontal(); }
            catch (Exception ex) { MarkUnavailable("BeginHorizontal", ex); }
        }

        public static void EndRow()
        {
            try { GUILayout.EndHorizontal(); }
            catch (Exception ex) { MarkUnavailable("EndHorizontal", ex); }
        }

        /// <summary>Blank vertical gap. GUILayout.Space is trivially replaced by an empty label.</summary>
        public static void Space(float atReference = 8f)
        {
            if (Available("Space"))
            {
                try { GUILayout.Space(Px(atReference)); return; }
                catch (Exception ex) { MarkUnavailable("Space", ex); }
            }
            Label(" ");
        }

        /// <summary>
        /// A checkbox. The fallback is a button labelled with its own state, which reads
        /// clearly enough and needs nothing but Button.
        /// </summary>
        public static bool Toggle(bool value, string text, float width = 0f)
        {
            if (Available("Toggle"))
            {
                try
                {
                    return width > 0f
                        ? GUILayout.Toggle(value, text, W(width))
                        : GUILayout.Toggle(value, text);
                }
                catch (Exception ex) { MarkUnavailable("Toggle", ex); }
            }

            return Button((value ? "[x] " : "[  ] ") + text, width) ? !value : value;
        }

        /// <summary>
        /// A text box. Where TextField is unavailable there is no way to type in IMGUI at
        /// all, so the fallback pastes from the clipboard instead - which is how a URL
        /// reaches the game in practice anyway.
        /// </summary>
        public static string TextField(string value, string emptyHint = "")
        {
            if (Available("TextField"))
            {
                try { return GUILayout.TextField(value ?? string.Empty); }
                catch (Exception ex) { MarkUnavailable("TextField", ex); }
            }

            BeginRow();
            Label(string.IsNullOrEmpty(value) ? (emptyHint.Length > 0 ? emptyHint : "(leer)") : value);
            string result = value;
            if (Button("Einfügen", 110f))
            {
                try { result = GUIUtility.systemCopyBuffer ?? value; }
                catch { /* clipboard unavailable - keep what we had */ }
            }
            if (Button("Leeren", 90f)) result = string.Empty;
            EndRow();
            return result;
        }

        /// <summary>
        /// A slider. The fallback steps the value with buttons, which is less pretty but
        /// perfectly usable for a volume control.
        /// </summary>
        public static float Slider(float value, float min, float max, float step = 0.05f)
        {
            if (Available("HorizontalSlider"))
            {
                try { return GUILayout.HorizontalSlider(value, min, max); }
                catch (Exception ex) { MarkUnavailable("HorizontalSlider", ex); }
            }

            BeginRow();
            if (Button("−", 40f)) value -= step;
            Label($"{value * 100f:0} %", 70f);
            if (Button("+", 40f)) value += step;
            EndRow();
            return Math.Min(Math.Max(value, min), max);
        }
    }
}

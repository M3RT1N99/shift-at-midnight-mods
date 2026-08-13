using System;

namespace MidnightRadio
{
    /// <summary>
    /// The only place that knows which mod loader we are running under. Everything else in
    /// the project is plain Unity and C#, so switching from MelonLoader to BepInEx means
    /// replacing this file and Main.cs - nothing more.
    ///
    /// Nothing here ever throws. Logging must never be the reason a game frame dies.
    /// </summary>
    internal static class Log
    {
        private static Action<string> _info  = _ => { };
        private static Action<string> _warn  = _ => { };
        private static Action<string> _error = _ => { };

        public static bool DebugEnabled { get; set; }

        /// <summary>Called once from the loader entry point with that loader's logger.</summary>
        public static void Bind(Action<string> info, Action<string> warn, Action<string> error)
        {
            if (info  != null) _info  = info;
            if (warn  != null) _warn  = warn;
            if (error != null) _error = error;
        }

        public static void Info(string msg)  { try { _info("[MidnightRadio] "  + msg); } catch { } }
        public static void Warn(string msg)  { try { _warn("[MidnightRadio] "  + msg); } catch { } }
        public static void Error(string msg) { try { _error("[MidnightRadio] " + msg); } catch { } }

        public static void Debug(string msg)
        {
            if (!DebugEnabled) return;
            try { _info("[MidnightRadio][dbg] " + msg); } catch { }
        }

        /// <summary>
        /// Runs an action and swallows anything it throws. Used at every boundary the game
        /// calls into us (update loops, scene hooks, network callbacks) so a bug in the mod
        /// degrades the radio instead of taking down the session.
        /// </summary>
        public static void Guard(string context, Action action)
        {
            try { action(); }
            catch (Exception ex) { Error($"{context}: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static T Guard<T>(string context, Func<T> func, T fallback = default)
        {
            try { return func(); }
            catch (Exception ex) { Error($"{context}: {ex.GetType().Name}: {ex.Message}"); return fallback; }
        }
    }
}

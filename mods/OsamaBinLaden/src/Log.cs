using System;

namespace OsamaBinLaden
{
    /// <summary>Exception-safe logging and game-callback boundaries.</summary>
    internal static class Log
    {
        private static Action<string> _info = _ => { };
        private static Action<string> _warn = _ => { };
        private static Action<string> _error = _ => { };

        public static bool DebugEnabled { get; set; }

        public static void Bind(Action<string> info, Action<string> warn, Action<string> error)
        {
            if (info != null) _info = info;
            if (warn != null) _warn = warn;
            if (error != null) _error = error;
        }

        public static void Info(string message) { try { _info("[OsamaBinLaden] " + message); } catch { } }
        public static void Warn(string message) { try { _warn("[OsamaBinLaden] " + message); } catch { } }
        public static void Error(string message) { try { _error("[OsamaBinLaden] " + message); } catch { } }

        public static void Debug(string message)
        {
            if (!DebugEnabled) return;
            try { _info("[OsamaBinLaden][dbg] " + message); } catch { }
        }

        public static void Guard(string context, Action action)
        {
            try { action(); }
            catch (Exception ex) { Error($"{context}: {ex.GetType().Name}: {ex.Message}"); }
        }

        public static T Guard<T>(string context, Func<T> action, T fallback = default)
        {
            try { return action(); }
            catch (Exception ex)
            {
                Error($"{context}: {ex.GetType().Name}: {ex.Message}");
                return fallback;
            }
        }
    }
}

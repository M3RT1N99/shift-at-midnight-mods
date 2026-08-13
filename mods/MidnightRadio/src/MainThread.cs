using System;
using System.Collections.Generic;

namespace MidnightRadio
{
    /// <summary>
    /// Small boundary between background file/process work and Unity. Unity objects may
    /// only be touched from the game thread, so workers post their completion here.
    /// </summary>
    internal static class MainThread
    {
        private static readonly Queue<Action> Pending = new();

        public static void Post(Action action)
        {
            if (action == null) return;
            lock (Pending) Pending.Enqueue(action);
        }

        public static void Drain(int maximum = 64)
        {
            for (int i = 0; i < maximum; i++)
            {
                Action action;
                lock (Pending)
                {
                    if (Pending.Count == 0) break;
                    action = Pending.Dequeue();
                }
                Log.Guard("main-thread callback", action);
            }
        }

        public static void Clear()
        {
            lock (Pending) Pending.Clear();
        }
    }
}

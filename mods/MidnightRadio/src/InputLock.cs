using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MidnightRadio
{
    /// <summary>
    /// Stops the player moving and looking around while the radio panel is open.
    ///
    /// The game already does this for its own menus: DialogueMenu holds a PlayerInput plus
    /// a previousActionMap field, so it switches action maps and switches back. We use the
    /// blunter half of the same API - PlayerInput.DeactivateInput() - because it needs no
    /// guess at which map name is the right one to switch to, and ActivateInput() is an
    /// exact inverse.
    ///
    /// The panel keeps working while input is off: it is drawn with IMGUI in OnGUI and
    /// reads its hotkey through the legacy Input class, neither of which goes through the
    /// Input System.
    ///
    /// Only inputs that were actually enabled get re-enabled, so this cannot switch on
    /// something the game had deliberately switched off.
    /// </summary>
    internal static class InputLock
    {
        private static readonly List<PlayerInput> Suspended = new();
        private static bool _locked;

        public static bool IsLocked => _locked;

        public static void Lock()
        {
            if (_locked) return;
            _locked = true;
            Suspended.Clear();

            Log.Guard("InputLock.Lock", () =>
            {
                foreach (var input in UnityEngine.Object.FindObjectsByType<PlayerInput>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (input == null || !input.inputIsActive) continue;
                    input.DeactivateInput();
                    Suspended.Add(input);
                }
                Log.Debug($"input suspended for {Suspended.Count} PlayerInput(s)");
            });

            // Whatever happened above, the cursor has to come back or the panel is unusable.
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }

        public static void Unlock()
        {
            if (!_locked) return;
            _locked = false;

            Log.Guard("InputLock.Unlock", () =>
            {
                foreach (var input in Suspended)
                {
                    // A scene change can destroy these while the panel is open.
                    if (input == null) continue;
                    input.ActivateInput();
                }
            });

            Suspended.Clear();
        }

        /// <summary>
        /// Re-asserts the cursor each frame while locked. The game reclaims it from its own
        /// Update, so setting it once on open is not enough.
        /// </summary>
        public static void Tick()
        {
            if (!_locked) return;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
        }
    }
}

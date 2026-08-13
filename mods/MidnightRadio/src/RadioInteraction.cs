using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace MidnightRadio
{
    /// <summary>
    /// Makes pressing the interact key on the placed radio open the mod's panel instead of
    /// toggling the built-in loop.
    ///
    /// The patch target is <c>Interactable.Interact(PlayerManager)</c>. That is the LOCAL
    /// entry point: the game calls it on the interacting client, which then fires
    /// Rpc_CMD_Interact -> Rpc_Interact for the networked effects. Returning false from a
    /// prefix therefore stops the whole chain, so opening your panel neither toggles the
    /// music nor does anything on the other players' machines - exactly right for a menu.
    ///
    /// The prefix binds __instance as UnityEngine.Component, a base class of Interactable.
    /// That avoids naming the interop namespace while still giving compile-time access to
    /// the transform hierarchy.
    ///
    /// "You have to buy the radio first" needs no code: the boombox is a purchasable decor
    /// item, so with none placed there is no Interactable to patch into and nothing happens.
    /// </summary>
    internal static class RadioInteraction
    {
        private const string HarmonyId = "io.github.m3rt1n99.midnightradio.interact";

        private static Func<GameObject> _radioRoot;
        private static Action _openPanel;
        private static object _harmony;
        private static bool _applied;

        public static bool Applied => _applied;

        public static bool Apply(Func<GameObject> radioRoot, Action openPanel)
        {
            if (_applied) return true;
            _radioRoot = radioRoot;
            _openPanel = openPanel;

            try
            {
                var interactableType = FindType("Il2Cpp.Interactable") ?? FindType("Interactable");
                if (interactableType == null)
                {
                    Log.Warn("Interactable not found - the radio panel stays on the hotkey");
                    return false;
                }

                // Interact(PlayerManager) - matched by name and arity so the parameter type
                // never has to be named.
                var target = interactableType.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "Interact" && m.GetParameters().Length == 1);
                if (target == null)
                {
                    Log.Warn("Interactable.Interact not found - the radio panel stays on the hotkey");
                    return false;
                }

                var harmonyType = FindType("HarmonyLib.Harmony");
                var harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                if (harmonyType == null || harmonyMethodType == null)
                {
                    Log.Warn("HarmonyLib unavailable - the radio panel stays on the hotkey");
                    return false;
                }

                _harmony = Activator.CreateInstance(harmonyType, HarmonyId);

                var prefix = typeof(RadioInteraction).GetMethod(
                    nameof(BeforeInteract), BindingFlags.NonPublic | BindingFlags.Static);
                var wrapped = Activator.CreateInstance(harmonyMethodType, prefix);

                var patch = harmonyType.GetMethods()
                    .First(m => m.Name == "Patch" && m.GetParameters().Length >= 3);

                var args = new object[patch.GetParameters().Length];
                args[0] = target;
                args[1] = wrapped;                       // prefix slot
                for (int i = 2; i < args.Length; i++) args[i] = null;

                patch.Invoke(_harmony, args);

                _applied = true;
                Log.Info("radio interaction hook installed - press interact on the radio");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("could not hook the radio interaction (" + Unwrap(ex).Message
                         + ") - the panel stays on the hotkey");
                return false;
            }
        }

        public static void Remove()
        {
            if (!_applied || _harmony == null) return;
            Log.Guard("RadioInteraction.Remove", () =>
                _harmony.GetType().GetMethod("UnpatchSelf")?.Invoke(_harmony, null));
            _applied = false;
            _radioRoot = null;
            _openPanel = null;
        }

        /// <summary>
        /// Returns false to swallow the interaction when it belongs to our radio. Never
        /// throws: an exception here would surface inside the game's interaction handling,
        /// so anything unexpected lets the original run.
        /// </summary>
        private static bool BeforeInteract(Component __instance)
        {
            try
            {
                if (__instance == null || _openPanel == null) return true;

                var root = _radioRoot?.Invoke();
                if (root == null) return true;

                if (!IsUnder(__instance.transform, root.transform)) return true;

                _openPanel();
                return false;
            }
            catch (Exception ex)
            {
                Log.Error("radio interaction prefix: " + ex.Message);
                return true;
            }
        }

        private static bool IsUnder(Transform node, Transform root)
        {
            for (var walk = node; walk != null; walk = walk.parent)
                if (ReferenceEquals(walk, root) || walk == root) return true;
            return false;
        }

        /// <summary>
        /// Relabels the interaction prompt, which otherwise still reads "Toggle Music".
        /// Best-effort: a missing field is not worth failing over.
        /// </summary>
        public static void RelabelPrompt(GameObject radioRoot, string text)
        {
            if (radioRoot == null) return;

            Log.Guard("relabel interact prompt", () =>
            {
                var interactableType = FindType("Il2Cpp.Interactable") ?? FindType("Interactable");
                if (interactableType == null) return;

                var field = interactableType.GetField("interactText",
                    BindingFlags.Public | BindingFlags.Instance);
                if (field == null) return;

                foreach (var component in radioRoot.GetComponentsInChildren<Component>(true))
                {
                    if (component == null) continue;
                    if (!interactableType.IsInstanceOfType(component)) continue;
                    field.SetValue(component, text);
                }
            });
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false, ignoreCase: false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static Exception Unwrap(Exception ex) =>
            ex is TargetInvocationException { InnerException: not null } inner
                ? inner.InnerException
                : ex;
    }
}

using System;
using UnityEngine;
using UnityEngine.Audio;

namespace MidnightRadio
{
    /// <summary>
    /// Finds the in-world radio and takes over its audio.
    ///
    /// Verified layout of the shipped prefab (resources.assets #5885):
    ///
    ///   Boombox Placed
    ///   +- GFX / EntranceAnim / SM_Prop_Computer_Radio_01   (the visible radio mesh)
    ///   +- Music Audio                 <- AudioSource #70843, the looping song
    ///   |    +- Animator "Music Audio" <- animates AudioSource.m_Volume between 0 and 0.15
    ///   +- Click SFX
    ///   +- Cube / Interactable         <- interactText "Toggle Music"
    ///
    /// Two facts drive this whole class:
    ///
    /// 1. The "Toggle Music" interaction does NOT play or stop anything. The source has
    ///    playOnAwake=1 and loop=1 and runs forever; the Animator only animates the VOLUME.
    ///    An active Animator state rewrites m_Volume every frame, so anything we write to
    ///    .volume on the original source is silently stomped. We therefore never touch it.
    ///    We mute the original instead - m_Mute is not animated.
    ///
    /// 2. Because the Interactable has onlyInvokeEventLocally=false, that toggle already
    ///    travels through Rpc_CMD_Interact -> Rpc_Interact and fires on every client. The
    ///    animated volume is therefore a free, already-replicated on/off signal. We read it
    ///    rather than syncing on/off ourselves.
    /// </summary>
    internal sealed class RadioTarget
    {
        private const string PrefabName = "Boombox Placed";
        private const string MusicChild = "Music Audio";

        /// <summary>Volume above which we consider the game's radio switched "on".</summary>
        private const float OnThreshold = 0.02f;

        public GameObject Root { get; private set; }
        public AudioSource Original { get; private set; }
        public AudioSource Playback { get; private set; }

        private bool _originalMute;

        public bool IsValid => Root != null && IsLiveSceneObject(Root) &&
                               Original != null && Original.enabled && Playback != null;

        /// <summary>
        /// True while the player has the radio switched on, derived from the animated
        /// volume of the original source. Already consistent across all clients.
        /// </summary>
        public bool SwitchedOn => Original != null && Original.volume > OnThreshold;

        public static RadioTarget TryResolve()
        {
            AudioSource original = null;
            GameObject root = null;

            foreach (var src in UnityEngine.Object.FindObjectsByType<AudioSource>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (src == null || !src.enabled || src.gameObject == null) continue;
                if (!IsLiveSceneObject(src.gameObject)) continue;
                if (!string.Equals(src.gameObject.name, MusicChild, StringComparison.Ordinal)) continue;

                var parent = src.transform.parent;
                if (parent == null) continue;
                if (parent.gameObject.name.IndexOf("Boombox", StringComparison.OrdinalIgnoreCase) < 0) continue;

                original = src;
                root = parent.gameObject;
                break;
            }

            if (original == null)
            {
                // Fall back to a name-only search so a renamed prefab still resolves.
                foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(
                             FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (go == null) continue;
                    if (!IsLiveSceneObject(go)) continue;
                    if (!go.name.StartsWith(PrefabName, StringComparison.Ordinal)) continue;

                    var t = go.transform.Find(MusicChild);
                    if (t == null) continue;

                    var src = t.GetComponent<AudioSource>();
                    if (src == null || !src.enabled) continue;

                    original = src;
                    root = go;
                    break;
                }
            }

            if (original == null) return null;

            var target = new RadioTarget
            {
                Root = root,
                Original = original,
                _originalMute = original.mute,
            };
            target.AttachPlayback();
            return target;
        }

        private static bool IsLiveSceneObject(GameObject value) =>
            value != null && value.activeInHierarchy && value.scene.IsValid() && value.scene.isLoaded;

        /// <summary>
        /// Creates our own AudioSource as a sibling under the same GameObject, copying the
        /// original's spatial settings so our music comes out of the radio in world space,
        /// and copying its mixer group so the player's Music slider keeps working.
        /// Copying the group beats looking it up by name - it survives asset reshuffles.
        /// </summary>
        private void AttachPlayback()
        {
            var host = new GameObject("MidnightRadio Playback");
            host.transform.SetParent(Original.transform, worldPositionStays: false);
            host.transform.localPosition = Vector3.zero;

            var src = host.AddComponent<AudioSource>();

            src.outputAudioMixerGroup = Original.outputAudioMixerGroup;   // "Music" group
            src.spatialBlend          = Original.spatialBlend;
            src.rolloffMode           = Original.rolloffMode;
            src.minDistance           = Original.minDistance;             // 1 m
            src.maxDistance           = Original.maxDistance;             // 50 m
            // Playback is synchronized against a shared clock. Per-listener Doppler would
            // continuously alter the resampling ratio as players move and defeat that.
            src.dopplerLevel          = 0f;
            src.spread                = Original.spread;
            src.priority              = Original.priority;

            src.playOnAwake = false;
            src.loop        = false;
            src.volume      = 0f;                                         // faded in on play

            Playback = src;
        }

        /// <summary>
        /// Silences the shipped loop without fighting the Animator. m_Mute is not animated,
        /// unlike m_Volume, so this sticks.
        /// </summary>
        public void SuppressOriginal(bool suppress)
        {
            if (Original == null) return;
            Original.mute = suppress || _originalMute;
        }

        /// <summary>Restores the game's own audio exactly as it was.</summary>
        public void Release()
        {
            SuppressOriginal(false);

            if (Playback != null)
            {
                Playback.Stop();
                UnityEngine.Object.Destroy(Playback.gameObject);
                Playback = null;
            }

            Original = null;
            Root = null;
        }
    }
}

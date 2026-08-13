using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace OsamaBinLaden
{
    /// <summary>
    /// Runtime-only tuning for the local character. Keeping this independent from the
    /// persisted config makes the character easy to smoke-test without loading the game.
    /// </summary>
    internal sealed class RuntimeCharacterOptions
    {
        public float RunSpeed { get; set; } = 7f;
        public float TriggerDistance { get; set; } = 2.2f;
        public float FuseSeconds { get; set; } = 0.75f;
        public float MaximumLifetimeSeconds { get; set; } = 30f;
        public float VisualScale { get; set; } = 1f;
        public bool ScreamEnabled { get; set; } = true;
        public float ScreamVolume { get; set; } = 0.65f;
        public float ExplosionVisualRadius { get; set; } = 5f;

        internal RuntimeCharacterOptions SanitizedCopy()
        {
            return new RuntimeCharacterOptions
            {
                RunSpeed = ClampFinite(RunSpeed, 0.5f, 20f, 7f),
                TriggerDistance = ClampFinite(TriggerDistance, 0.25f, 10f, 2.2f),
                FuseSeconds = ClampFinite(FuseSeconds, 0f, 10f, 0.75f),
                MaximumLifetimeSeconds = ClampFinite(MaximumLifetimeSeconds, 1f, 300f, 30f),
                VisualScale = ClampFinite(VisualScale, 0.25f, 3f, 1f),
                ScreamEnabled = ScreamEnabled,
                ScreamVolume = ClampFinite(ScreamVolume, 0f, 1f, 0.65f),
                ExplosionVisualRadius = ClampFinite(ExplosionVisualRadius, 0.5f, 20f, 5f),
            };
        }

        private static float ClampFinite(float value, float min, float max, float fallback)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return fallback;
            return Math.Max(min, Math.Min(max, value));
        }
    }

    /// <summary>
    /// A deliberately local, non-networked character assembled from Unity primitives.
    /// The owning controller calls <see cref="Tick"/> from the Unity main thread and owns
    /// all game-state decisions (hunt detection, solo-mode gate and player damage).
    /// </summary>
    internal sealed class RuntimeCharacter : IDisposable
    {
        internal readonly struct DetonationInfo
        {
            public DetonationInfo(Vector3 position, Transform target, float distanceToTarget)
            {
                Position = position;
                Target = target;
                DistanceToTarget = distanceToTarget;
            }

            public Vector3 Position { get; }
            public Transform Target { get; }
            public float DistanceToTarget { get; }
        }

        private const float TurnSpeedDegrees = 540f;

        private readonly RuntimeCharacterOptions _options;
        private readonly Action<DetonationInfo> _onDetonated;
        private readonly List<Material> _materials = new List<Material>();

        private GameObject _root;
        private Transform _target;
        private Transform _body;
        private Transform _leftArm;
        private Transform _rightArm;
        private Transform _leftLeg;
        private Transform _rightLeg;
        private AudioSource _screamSource;
        private AudioClip _screamClip;
        private LocalExplosionEffect _explosion;
        private float _stridePhase;
        private float _lifetime;
        private float _fuseElapsed;
        private bool _fuseStarted;
        private bool _detonated;
        private bool _disposed;

        public RuntimeCharacter(
            Transform target,
            Vector3 spawnPosition,
            RuntimeCharacterOptions options,
            Action<DetonationInfo> onDetonated)
        {
            _target = target;
            _options = (options ?? new RuntimeCharacterOptions()).SanitizedCopy();
            _onDetonated = onDetonated;

            try
            {
                BuildCharacter(spawnPosition);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public bool IsDetonated => _detonated;

        /// <summary>True once cancelled, or after the cosmetic blast has completed.</summary>
        public bool IsFinished =>
            _disposed || (_detonated && (_explosion == null || _explosion.IsFinished));

        public Vector3 Position
        {
            get
            {
                if (_root != null) return _root.transform.position;
                if (_explosion != null) return _explosion.Position;
                return Vector3.zero;
            }
        }

        public void SetTarget(Transform target)
        {
            if (_disposed || _detonated) return;
            _target = target;
        }

        /// <summary>
        /// Advances pursuit, animation and the post-detonation visual. Call on Unity's main
        /// thread only. The caller must wrap its game Update boundary with Log.Guard.
        /// </summary>
        public void Tick(float deltaTime)
        {
            if (_disposed) return;

            float dt = SanitizeDeltaTime(deltaTime);

            if (_detonated)
            {
                TickExplosion(dt);
                return;
            }

            _lifetime += dt;
            if (_lifetime >= _options.MaximumLifetimeSeconds)
            {
                Detonate();
                return;
            }

            if (_fuseStarted)
            {
                _fuseElapsed += dt;
                if (_fuseElapsed >= _options.FuseSeconds)
                {
                    Detonate();
                    return;
                }
            }

            if (_root == null)
            {
                Dispose();
                return;
            }

            if (_target == null)
            {
                AnimateStride(dt, false);
                return;
            }

            Vector3 current = _root.transform.position;
            Vector3 targetPosition = _target.position;
            Vector3 offset = targetPosition - current;
            float distance = offset.magnitude;

            if (!_fuseStarted && distance <= _options.TriggerDistance)
            {
                _fuseStarted = true;
                _fuseElapsed = 0f;
                if (_options.FuseSeconds <= 0f)
                {
                    Detonate();
                    return;
                }
            }

            Vector3 planarDirection = new Vector3(offset.x, 0f, offset.z);
            if (planarDirection.sqrMagnitude > 0.0001f)
            {
                Quaternion desiredRotation = Quaternion.LookRotation(planarDirection.normalized, Vector3.up);
                _root.transform.rotation = Quaternion.RotateTowards(
                    _root.transform.rotation,
                    desiredRotation,
                    TurnSpeedDegrees * dt);
            }

            float step = _options.RunSpeed * dt;
            Vector3 next = Vector3.MoveTowards(current, targetPosition, step);
            bool moved = (next - current).sqrMagnitude > 0.000001f;
            _root.transform.position = next;
            AnimateStride(dt, moved);

            // A large frame can cross the fuse radius, so check again after movement.
            if (!_fuseStarted && (_target.position - next).sqrMagnitude <=
                _options.TriggerDistance * _options.TriggerDistance)
            {
                _fuseStarted = true;
                _fuseElapsed = 0f;
            }
        }

        /// <summary>
        /// Starts the local blast exactly once. Damage is intentionally delegated to the
        /// owner through the callback so this class never touches game health/network code.
        /// </summary>
        public void Detonate()
        {
            if (_disposed || _detonated) return;
            _detonated = true;

            Vector3 position = _root != null ? _root.transform.position : Vector3.zero;
            Transform target = _target;
            float distance = float.PositiveInfinity;
            if (target != null) distance = Vector3.Distance(position, target.position);

            StopScream();
            DestroyCharacterVisuals();
            try
            {
                _explosion = new LocalExplosionEffect(position, _options.ExplosionVisualRadius);
            }
            catch (Exception ex)
            {
                // A missing renderer/shader must not suppress the gameplay callback.
                try { Log.Warn($"cosmetic explosion unavailable ({ex.Message})"); }
                catch { }
                _explosion = null;
            }

            // State is already committed before calling outside code. If damage handling
            // fails, this character cannot detonate a second time on the following frame.
            _onDetonated?.Invoke(new DetonationInfo(position, target, distance));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            StopScream();
            DestroyCharacterVisuals();

            if (_explosion != null)
            {
                _explosion.Dispose();
                _explosion = null;
            }

            _target = null;
        }

        private void BuildCharacter(Vector3 spawnPosition)
        {
            _root = new GameObject("Osama Bin Laden (satirical local NPC)");
            _root.transform.position = spawnPosition;
            _root.transform.localScale = Vector3.one * _options.VisualScale;

            Color robe = new Color(0.56f, 0.48f, 0.32f, 1f);
            Color robeLight = new Color(0.72f, 0.66f, 0.48f, 1f);
            Color skin = new Color(0.55f, 0.39f, 0.27f, 1f);
            Color dark = new Color(0.055f, 0.045f, 0.035f, 1f);
            Color cloth = new Color(0.88f, 0.86f, 0.75f, 1f);

            _body = CreatePart("Body", PrimitiveType.Capsule, _root.transform,
                new Vector3(0f, 1.25f, 0f), new Vector3(0.72f, 0.82f, 0.54f), robe);

            CreatePart("Robe", PrimitiveType.Cylinder, _root.transform,
                new Vector3(0f, 0.82f, 0f), new Vector3(0.48f, 0.54f, 0.42f), robeLight);

            CreatePart("Head", PrimitiveType.Sphere, _root.transform,
                new Vector3(0f, 2.15f, 0f), new Vector3(0.52f, 0.58f, 0.52f), skin);

            CreatePart("Beard", PrimitiveType.Cube, _root.transform,
                new Vector3(0f, 1.94f, 0.245f), new Vector3(0.38f, 0.42f, 0.12f), dark);

            CreatePart("Turban", PrimitiveType.Cylinder, _root.transform,
                new Vector3(0f, 2.49f, 0f), new Vector3(0.37f, 0.105f, 0.37f), cloth);

            CreatePart("TurbanTop", PrimitiveType.Sphere, _root.transform,
                new Vector3(0f, 2.56f, 0f), new Vector3(0.58f, 0.20f, 0.52f), cloth);

            _leftArm = CreatePart("LeftArm", PrimitiveType.Capsule, _root.transform,
                new Vector3(-0.53f, 1.35f, 0f), new Vector3(0.22f, 0.62f, 0.22f), robe);
            _rightArm = CreatePart("RightArm", PrimitiveType.Capsule, _root.transform,
                new Vector3(0.53f, 1.35f, 0f), new Vector3(0.22f, 0.62f, 0.22f), robe);
            _leftLeg = CreatePart("LeftLeg", PrimitiveType.Capsule, _root.transform,
                new Vector3(-0.22f, 0.45f, 0f), new Vector3(0.24f, 0.50f, 0.24f), dark);
            _rightLeg = CreatePart("RightLeg", PrimitiveType.Capsule, _root.transform,
                new Vector3(0.22f, 0.45f, 0f), new Vector3(0.24f, 0.50f, 0.24f), dark);

            try
            {
                CreateScreamSource();
            }
            catch (Exception ex)
            {
                // Audio is optional; pursuit and detonation remain usable without it.
                StopScream();
                try { Log.Warn($"procedural scream unavailable ({ex.Message})"); }
                catch { }
            }
        }

        private Transform CreatePart(
            string name,
            PrimitiveType primitiveType,
            Transform parent,
            Vector3 localPosition,
            Vector3 localScale,
            Color color)
        {
            GameObject part = GameObject.CreatePrimitive(primitiveType);
            part.name = name;
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = Quaternion.identity;
            part.transform.localScale = localScale;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                // Disable immediately; Destroy is deferred until the end of the frame.
                collider.enabled = false;
                SafeDestroy(collider);
            }

            Renderer renderer = part.GetComponent<Renderer>();
            if (renderer != null)
            {
                Material material = renderer.material;
                if (material != null)
                {
                    material.color = color;
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                    if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    _materials.Add(material);
                }
            }

            return part.transform;
        }

        private void CreateScreamSource()
        {
            if (!_options.ScreamEnabled || _options.ScreamVolume <= 0f || _root == null) return;

            _screamClip = SynthesizeScream();
            _screamSource = _root.AddComponent<AudioSource>();
            _screamSource.playOnAwake = false;
            _screamSource.loop = true;
            _screamSource.clip = _screamClip;
            _screamSource.volume = _options.ScreamVolume;
            _screamSource.spatialBlend = 1f;
            _screamSource.dopplerLevel = 0f;
            _screamSource.rolloffMode = AudioRolloffMode.Linear;
            _screamSource.minDistance = 1.5f;
            _screamSource.maxDistance = Math.Max(18f, _options.ExplosionVisualRadius * 5f);
            _screamSource.priority = 96;
            _screamSource.Play();
        }

        private static AudioClip SynthesizeScream()
        {
            const int sampleRate = 24000;
            const float durationSeconds = 1.35f;
            int sampleCount = (int)(sampleRate * durationSeconds);
            float[] samples = new float[sampleCount];

            // Deterministic voice-like oscillator plus filtered-ish noise. This is original
            // generated PCM, not a recording or extracted game asset.
            uint noiseState = 0x51F15EEDu;
            double phase = 0d;
            float previousNoise = 0f;

            for (int i = 0; i < sampleCount; i++)
            {
                float time = i / (float)sampleRate;
                float progress = time / durationSeconds;
                float attack = Math.Min(1f, time / 0.035f);
                float release = Math.Min(1f, (durationSeconds - time) / 0.12f);
                float envelope = attack * release;

                double wobble = Math.Sin(time * Math.PI * 2d * 5.4d);
                double pitch = 510d + (210d * progress) + (62d * wobble);
                phase += Math.PI * 2d * pitch / sampleRate;

                noiseState ^= noiseState << 13;
                noiseState ^= noiseState >> 17;
                noiseState ^= noiseState << 5;
                float whiteNoise = ((noiseState & 0xFFFFu) / 32767.5f) - 1f;
                previousNoise = (previousNoise * 0.72f) + (whiteNoise * 0.28f);

                double voice =
                    Math.Sin(phase) +
                    (0.42d * Math.Sin((phase * 2.01d) + 0.3d)) +
                    (0.20d * Math.Sin((phase * 3.03d) + 0.8d));
                float tremolo = 0.84f + (0.16f * (float)Math.Sin(time * Math.PI * 2d * 9d));
                float sample = envelope * tremolo * ((float)voice * 0.28f + previousNoise * 0.13f);
                samples[i] = Math.Max(-0.78f, Math.Min(0.78f, sample));
            }

            AudioClip clip = AudioClip.Create(
                "OsamaBinLaden.ProceduralScream",
                sampleCount,
                1,
                sampleRate,
                false);

            if (clip == null) throw new InvalidOperationException("Unity could not create the scream clip.");

            Il2CppStructArray<float> pcm = new Il2CppStructArray<float>(samples);
            if (!clip.SetData(pcm, 0))
            {
                SafeDestroy(clip);
                throw new InvalidOperationException("Unity rejected the generated scream PCM.");
            }

            return clip;
        }

        private void AnimateStride(float deltaTime, bool moving)
        {
            if (!moving)
            {
                SetLimbRotation(_leftArm, 0f);
                SetLimbRotation(_rightArm, 0f);
                SetLimbRotation(_leftLeg, 0f);
                SetLimbRotation(_rightLeg, 0f);
                if (_body != null)
                {
                    Vector3 idle = _body.localPosition;
                    idle.y = 1.25f;
                    _body.localPosition = idle;
                }
                return;
            }

            _stridePhase += deltaTime * (5.5f + (_options.RunSpeed * 0.55f));
            float swing = (float)Math.Sin(_stridePhase) * 34f;
            SetLimbRotation(_leftArm, -swing);
            SetLimbRotation(_rightArm, swing);
            SetLimbRotation(_leftLeg, swing);
            SetLimbRotation(_rightLeg, -swing);

            if (_body != null)
            {
                Vector3 bobbed = _body.localPosition;
                bobbed.y = 1.25f + Math.Abs((float)Math.Sin(_stridePhase * 2f)) * 0.045f;
                _body.localPosition = bobbed;
            }
        }

        private static void SetLimbRotation(Transform limb, float xDegrees)
        {
            if (limb != null) limb.localRotation = Quaternion.Euler(xDegrees, 0f, 0f);
        }

        private void TickExplosion(float deltaTime)
        {
            if (_explosion == null) return;
            try
            {
                _explosion.Tick(deltaTime);
            }
            catch (Exception ex)
            {
                try { Log.Warn($"cosmetic explosion stopped ({ex.Message})"); }
                catch { }
                _explosion.Dispose();
            }
            if (!_explosion.IsFinished) return;

            _explosion.Dispose();
            _explosion = null;
        }

        private void StopScream()
        {
            if (_screamSource != null)
            {
                try
                {
                    _screamSource.Stop();
                    _screamSource.clip = null;
                }
                catch
                {
                    // Unity may already be tearing down this scene object.
                }
                _screamSource = null;
            }

            if (_screamClip != null)
            {
                SafeDestroy(_screamClip);
                _screamClip = null;
            }
        }

        private void DestroyCharacterVisuals()
        {
            if (_root != null)
            {
                SafeDestroy(_root);
                _root = null;
            }

            for (int i = 0; i < _materials.Count; i++) SafeDestroy(_materials[i]);
            _materials.Clear();

            _body = null;
            _leftArm = null;
            _rightArm = null;
            _leftLeg = null;
            _rightLeg = null;
        }

        private static float SanitizeDeltaTime(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0f) return 0f;
            return Math.Min(value, 0.1f);
        }

        private static void SafeDestroy(UnityEngine.Object value)
        {
            if (value == null) return;
            try { UnityEngine.Object.Destroy(value); }
            catch { }
        }

        /// <summary>A short-lived, local-only primitive blast with no collider or gameplay code.</summary>
        private sealed class LocalExplosionEffect : IDisposable
        {
            private const float DurationSeconds = 0.72f;

            private readonly List<BlastPart> _parts = new List<BlastPart>();
            private GameObject _root;
            private float _elapsed;
            private bool _disposed;

            public LocalExplosionEffect(Vector3 position, float radius)
            {
                Radius = Math.Max(0.5f, radius);

                try
                {
                    _root = new GameObject("OsamaBinLaden.LocalCosmeticExplosion");
                    _root.transform.position = position + (Vector3.up * 0.85f);

                    AddPart("Flash", Vector3.zero, new Color(1f, 0.92f, 0.34f, 1f), 1.00f, 0f);
                    AddPart("FireLeft", new Vector3(-0.18f, 0.12f, 0f),
                        new Color(1f, 0.30f, 0.035f, 1f), 0.72f, 0.06f);
                    AddPart("FireRight", new Vector3(0.20f, 0.02f, 0.08f),
                        new Color(1f, 0.49f, 0.06f, 1f), 0.62f, 0.10f);
                    AddPart("Smoke", new Vector3(0f, 0.35f, -0.08f),
                        new Color(0.13f, 0.11f, 0.10f, 1f), 0.82f, 0.18f);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            public float Radius { get; }
            public Vector3 Position => _root != null ? _root.transform.position : Vector3.zero;
            public bool IsFinished => _disposed || _elapsed >= DurationSeconds;

            public void Tick(float deltaTime)
            {
                if (_disposed || _root == null) return;
                _elapsed += Math.Max(0f, deltaTime);
                float progress = Math.Min(1f, _elapsed / DurationSeconds);
                float eased = 1f - ((1f - progress) * (1f - progress));

                for (int i = 0; i < _parts.Count; i++)
                {
                    BlastPart part = _parts[i];
                    float localProgress = Math.Max(0f, (progress - part.Delay) / (1f - part.Delay));
                    float pulse = localProgress < 0.72f
                        ? localProgress / 0.72f
                        : Math.Max(0f, 1f - ((localProgress - 0.72f) / 0.28f));
                    float diameter = Radius * 2f * part.SizeFactor * eased * Math.Max(0.025f, pulse);
                    if (part.Transform != null) part.Transform.localScale = Vector3.one * diameter;

                    if (part.Material != null)
                    {
                        Color faded = Color.Lerp(part.StartColor, new Color(0.08f, 0.07f, 0.06f, 1f), progress);
                        part.Material.color = faded;
                        if (part.Material.HasProperty("_BaseColor"))
                            part.Material.SetColor("_BaseColor", faded);
                    }
                }

                if (_elapsed >= DurationSeconds) Dispose();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                if (_root != null)
                {
                    SafeDestroy(_root);
                    _root = null;
                }

                for (int i = 0; i < _parts.Count; i++) SafeDestroy(_parts[i].Material);
                _parts.Clear();
            }

            private void AddPart(
                string name,
                Vector3 localPosition,
                Color color,
                float sizeFactor,
                float delay)
            {
                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = name;
                sphere.transform.SetParent(_root.transform, false);
                sphere.transform.localPosition = localPosition;
                sphere.transform.localScale = Vector3.one * 0.01f;

                Collider collider = sphere.GetComponent<Collider>();
                if (collider != null)
                {
                    collider.enabled = false;
                    SafeDestroy(collider);
                }

                Material material = null;
                Renderer renderer = sphere.GetComponent<Renderer>();
                if (renderer != null)
                {
                    material = renderer.material;
                    if (material != null)
                    {
                        material.color = color;
                        if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
                        if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                    }
                }

                _parts.Add(new BlastPart(sphere.transform, material, color, sizeFactor, delay));
            }

            private readonly struct BlastPart
            {
                public BlastPart(
                    Transform transform,
                    Material material,
                    Color startColor,
                    float sizeFactor,
                    float delay)
                {
                    Transform = transform;
                    Material = material;
                    StartColor = startColor;
                    SizeFactor = sizeFactor;
                    Delay = delay;
                }

                public Transform Transform { get; }
                public Material Material { get; }
                public Color StartColor { get; }
                public float SizeFactor { get; }
                public float Delay { get; }
            }
        }
    }
}

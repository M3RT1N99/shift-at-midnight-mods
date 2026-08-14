using System;
using System.IO;
using Il2Cpp;
using MelonLoader.Utils;
using OsamaBinLaden.Multiplayer;
using UnityEngine;

namespace OsamaBinLaden
{
    /// <summary>Owns the Hunt state machine and every runtime object, in solo and multiplayer.</summary>
    internal sealed class ModController : IDisposable
    {
        private readonly string _configPath;
        private readonly Config _config;
        private readonly System.Random _random = new System.Random();

        private RuntimeCharacter _character;
        private bool _huntObserved;
        private bool _spawnAttemptedThisHunt;
        private bool _gateWarningShown;
        private EncounterSession _multiplayer;
        private bool _disposed;

        public ModController()
        {
            string dataDirectory = Path.Combine(MelonEnvironment.UserDataDirectory, "OsamaBinLaden");
            Directory.CreateDirectory(dataDirectory);
            _configPath = Path.Combine(dataDirectory, "config.json");
            SeedDefaultConfig(dataDirectory);
            _config = Config.Load(_configPath);
            _config.Clamp();
            Log.DebugEnabled = string.Equals(
                _config.Logging.Level, "debug", StringComparison.OrdinalIgnoreCase);

            if (!File.Exists(_configPath)) _config.Save(_configPath);
        }

        private void SeedDefaultConfig(string dataDirectory)
        {
            if (File.Exists(_configPath)) return;

            string templatePath = Path.Combine(dataDirectory, "config.json.default");
            if (!File.Exists(templatePath)) return;

            try
            {
                File.Copy(templatePath, _configPath, overwrite: false);
                Log.Info("created config.json from the packaged default");
            }
            catch (Exception ex)
            {
                // Config.Load will still provide bounded in-code defaults.
                Log.Warn($"could not seed config.json ({ex.Message})");
            }
        }

        public void Update()
        {
            if (_disposed) return;

            if (!_config.Enabled || !_config.Spawn.Enabled)
            {
                ResetEncounter();
                _multiplayer?.Reset();
                return;
            }

            if (SessionGate.TryGetSoloPlayer(out PlayerManager player, out Transform target))
            {
                _gateWarningShown = false;
                _multiplayer?.Reset();
                UpdateSolo(HuntManager.Instance, target);
                return;
            }

            // Not a positively-confirmed solo session. Never keep a solo character alive on
            // the strength of a stale target reference while we decide what to do next.
            CleanupCharacter();
            _huntObserved = false;
            _spawnAttemptedThisHunt = false;

            if (TryUpdateMultiplayer()) return;

            _multiplayer?.Reset();
            if (!_gateWarningShown)
            {
                Log.Warn("inactive: the game has not positively confirmed a live solo or multiplayer session");
                _gateWarningShown = true;
            }
        }

        private void UpdateSolo(HuntManager hunt, Transform target)
        {
            if (hunt == null || !hunt || !hunt.isActiveAndEnabled)
            {
                ResetEncounter();
                return;
            }

            bool huntInProgress = hunt.huntInProgress;
            if (!huntInProgress)
            {
                if (_huntObserved) Log.Debug("Hunt ended; cleaning up local character");
                ResetEncounter();
                return;
            }

            if (!_huntObserved)
            {
                _huntObserved = true;
                TryStartEncounter(hunt, target);
            }

            if (_character == null) return;

            _character.SetTarget(target);
            _character.Tick(Time.deltaTime);
            if (_character.IsFinished) CleanupCharacter();
        }

        /// <summary>
        /// True once a live, positively-confirmed non-solo Fusion session was found and
        /// handed to the multiplayer encounter session, regardless of whether it did anything
        /// this tick. False tells the caller to fall back to the "inactive" warning, the same
        /// way an ambiguous solo session does.
        /// </summary>
        private bool TryUpdateMultiplayer()
        {
            if (_config.SinglePlayerOnly || _config.Safety.DisableInMultiplayer ||
                !_config.Safety.AllowNetworkSends)
                return false;

            _multiplayer ??= new EncounterSession(_config);
            if (!_multiplayer.IsActive)
            {
                _multiplayer.Reset();
                return false;
            }

            _multiplayer.Update(Time.deltaTime);
            return true;
        }

        private void TryStartEncounter(HuntManager hunt, Transform target)
        {
            if (_spawnAttemptedThisHunt) return;
            _spawnAttemptedThisHunt = true;

            if (_random.NextDouble() > _config.Spawn.ChancePerEligibleEncounter)
            {
                Log.Debug("Hunt encounter skipped by configured chance");
                return;
            }

            Vector3 spawnPosition = ResolveSpawnPosition(hunt, target);
            var options = new RuntimeCharacterOptions
            {
                RunSpeed = _config.Attack.RunSpeedMetersPerSecond,
                TriggerDistance = _config.Attack.DetonationDistanceMeters,
                FuseSeconds = _config.Attack.FuseSeconds,
                MaximumLifetimeSeconds = _config.Spawn.MaximumLifetimeSeconds,
                VisualScale = _config.Effects.VisualScale,
                ScreamEnabled = _config.Effects.ScreamEnabled,
                ScreamVolume = _config.Effects.ScreamVolume,
                ExplosionVisualRadius = _config.Effects.ExplosionRadiusMeters
            };

            _character = new RuntimeCharacter(target, spawnPosition, options, OnDetonated);
            Log.Info($"Hunt encounter started at {Vector3.Distance(spawnPosition, target.position):0.0} m");
        }

        private Vector3 ResolveSpawnPosition(HuntManager hunt, Transform target)
        {
            var points = hunt.huntSpawnPoints;
            int count = points != null ? points.Length : 0;
            return SpawnPlacement.Resolve(
                count,
                index => points[index],
                target.position,
                target.forward,
                _config.Spawn.MinimumSpawnDistanceMeters,
                _config.Spawn.MaximumSpawnDistanceMeters);
        }

        private void OnDetonated(RuntimeCharacter.DetonationInfo info)
        {
            // Re-prove solo mode at the irreversible gameplay boundary. The target in the
            // callback is only visual state and is never trusted for damage authority.
            if (!SessionGate.TryGetSoloPlayer(out PlayerManager player, out Transform currentTarget))
            {
                Log.Warn("detonation stayed cosmetic because solo mode could not be re-confirmed");
                return;
            }

            float distance = Vector3.Distance(info.Position, currentTarget.position);
            float damage = ExplosionMath.CalculateDamage(
                distance,
                _config.Attack.DetonationDistanceMeters,
                _config.Effects.ExplosionRadiusMeters,
                _config.Effects.ExplosionDamage);

            if (damage <= 0f || player.dead) return;

            player.TakeDamage(damage, true, "Explosion");
            Log.Info($"detonated {distance:0.0} m from the player ({damage:0.#} damage)");
        }

        public void SceneChanged()
        {
            if (_disposed) return;
            ResetEncounter();
            _multiplayer?.Reset();
            _gateWarningShown = false;
        }

        private void ResetEncounter()
        {
            CleanupCharacter();
            _huntObserved = false;
            _spawnAttemptedThisHunt = false;
        }

        private void CleanupCharacter()
        {
            if (_character == null) return;
            _character.Dispose();
            _character = null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CleanupCharacter();
            _multiplayer?.Dispose();
            _multiplayer = null;
            _config.Save(_configPath);
        }
    }
}

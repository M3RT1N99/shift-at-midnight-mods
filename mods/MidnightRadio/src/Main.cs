using System;
using MelonLoader;

// MelonLoader reads the version from THIS attribute and nowhere else - not from the
// csproj, not from the manifest. It sat at 1.0.0 while everything else said 1.1.0, so the
// loader log and the mod manager disagreed. build.ps1 now fails if this drifts from
// mod.json again.
[assembly: MelonInfo(typeof(MidnightRadio.MidnightRadioMod), "Midnight Radio",
    MidnightRadio.BuildVersion.Value, "Marti")]
[assembly: VerifyLoaderVersion(0, 7, 3, true)]

namespace MidnightRadio
{
    /// <summary>MelonLoader lifecycle boundary; all callbacks degrade instead of escaping.</summary>
    public sealed class MidnightRadioMod : MelonMod
    {
        private ModController _controller;

        public override void OnInitializeMelon()
        {
            Log.Bind(
                message => LoggerInstance.Msg(message),
                message => LoggerInstance.Warning(message),
                message => LoggerInstance.Error(message));

            Log.Guard("initialize", () =>
            {
                _controller = new ModController();
                Log.Info("ready; press F4 for the radio panel");
                Log.Warn("multiplayer sync is disabled until the Fusion receive-path probe passes");
            });
        }

        public override void OnUpdate() =>
            Log.Guard("update", () => _controller?.Update());

        public override void OnGUI() =>
            Log.Guard("gui", () => _controller?.Draw());

        public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
            Log.Guard("scene loaded", () => _controller?.SceneChanged());

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName) =>
            Log.Guard("scene unloaded", () => _controller?.SceneChanged());

        public override void OnDeinitializeMelon() => Shutdown();

        private void Shutdown()
        {
            Log.Guard("shutdown", () => _controller?.Dispose());
            _controller = null;
        }
    }
}

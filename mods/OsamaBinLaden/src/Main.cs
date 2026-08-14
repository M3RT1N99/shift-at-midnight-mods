using MelonLoader;

[assembly: MelonInfo(typeof(OsamaBinLaden.OsamaBinLadenMod), "Osama Bin Laden NPC", "0.2.0", "Marti")]
[assembly: VerifyLoaderVersion(0, 7, 3, true)]

namespace OsamaBinLaden
{
    /// <summary>MelonLoader lifecycle boundary.</summary>
    public sealed class OsamaBinLadenMod : MelonMod
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
                Log.Info("ready; solo and, if permitted by config, multiplayer Hunt encounters");
            });
        }

        public override void OnUpdate() =>
            Log.Guard("update", () => _controller?.Update());

        public override void OnSceneWasLoaded(int buildIndex, string sceneName) =>
            Log.Guard("scene loaded", () => _controller?.SceneChanged());

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName) =>
            Log.Guard("scene unloaded", () => _controller?.SceneChanged());

        public override void OnDeinitializeMelon()
        {
            Log.Guard("shutdown", () => _controller?.Dispose());
            _controller = null;
        }
    }
}

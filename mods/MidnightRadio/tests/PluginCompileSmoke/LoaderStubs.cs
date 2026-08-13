using System;
using System.Collections;

namespace MelonLoader
{
    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class MelonInfoAttribute : Attribute
    {
        public MelonInfoAttribute(Type type, string name, string version, string author) { }
    }

    [AttributeUsage(AttributeTargets.Assembly)]
    internal sealed class VerifyLoaderVersionAttribute : Attribute
    {
        public VerifyLoaderVersionAttribute(int major, int minor, int patch, bool isMinimum) { }
    }

    public class MelonLogger
    {
        public sealed class Instance
        {
            public void Msg(string message) { }
            public void Warning(string message) { }
            public void Error(string message) { }
        }
    }

    public abstract class MelonMod
    {
        protected MelonLogger.Instance LoggerInstance { get; } = new();
        public virtual void OnInitializeMelon() { }
        public virtual void OnUpdate() { }
        public virtual void OnGUI() { }
        public virtual void OnSceneWasLoaded(int buildIndex, string sceneName) { }
        public virtual void OnSceneWasUnloaded(int buildIndex, string sceneName) { }
        public virtual void OnApplicationQuit() { }
        public virtual void OnDeinitializeMelon() { }
    }

    internal static class MelonCoroutines
    {
        public static object Start(IEnumerator routine) => routine;
    }
}

namespace MelonLoader.Utils
{
    public static class MelonEnvironment
    {
        public static string UserDataDirectory => System.AppContext.BaseDirectory;
    }
}

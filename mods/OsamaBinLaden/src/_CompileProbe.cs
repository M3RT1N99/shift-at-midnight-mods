extern alias il2cpp;

using Il2CppFusion;
using Il2CppFusion.Sockets;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace OsamaBinLaden
{
    internal sealed class CompileProbe
    {
        private NetworkDelegates _delegates;
        private INetworkRunnerCallbacks _callbacks;
        private System.Action<NetworkRunner, PlayerRef, ReliableKey, il2cpp::Il2CppSystem.ArraySegment<byte>> _managed;
        private il2cpp::Il2CppSystem.Action<NetworkRunner, PlayerRef, ReliableKey, il2cpp::Il2CppSystem.ArraySegment<byte>> _native;

        public void Add(NetworkRunner runner)
        {
            _delegates = new NetworkDelegates();
            _managed = Received;
            _native = DelegateSupport.ConvertDelegate<
                il2cpp::Il2CppSystem.Action<NetworkRunner, PlayerRef, ReliableKey,
                    il2cpp::Il2CppSystem.ArraySegment<byte>>>(_managed);
            _delegates.OnReliableDataReceived = _native;
            _callbacks = _delegates.Cast<INetworkRunnerCallbacks>();
            runner.AddCallbacks(_callbacks);
        }

        public void Remove(NetworkRunner runner) => runner.RemoveCallbacks(_callbacks);

        public bool Publish(NetworkRunner runner)
        {
            var properties = new Il2CppSystem.Collections.Generic.Dictionary<string, SessionProperty>();
            properties.Add("obln", (SessionProperty)1);
            return runner.SessionInfo != null && runner.SessionInfo.IsValid &&
                   runner.SessionInfo.UpdateCustomProperties(properties);
        }

        public bool Marker(NetworkRunner runner)
        {
            SessionProperty marker;
            return runner.SessionInfo != null && runner.SessionInfo.IsValid &&
                   runner.SessionInfo.Properties != null &&
                   runner.SessionInfo.Properties.TryGetValue("obln", out marker) &&
                   marker != null && marker.IsInt && (int)marker == 1;
        }

        private void Received(NetworkRunner runner, PlayerRef player, ReliableKey key,
            il2cpp::Il2CppSystem.ArraySegment<byte> data) { }
    }
}

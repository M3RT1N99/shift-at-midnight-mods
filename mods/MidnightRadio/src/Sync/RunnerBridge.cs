using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// Narrow, fail-closed adapter around Photon Fusion's generated IL2CPP wrappers.
    /// Reflection keeps the rest of the mod buildable without baking a particular Fusion
    /// package into it. All lookups happen on the main thread and are cached per runner.
    ///
    /// Sending remains disabled until the separately verified receive hook calls
    /// <see cref="MarkReceiveReady"/>. A client that can transmit but cannot receive state
    /// is worse than local-only playback, so this gate must never be bypassed.
    /// </summary>
    internal static class RunnerBridge
    {
        private static readonly object Gate = new();

        private static Type _runnerType;
        private static object _runner;
        private static object _reliableKey;
        private static bool _receiveReady;
        private static bool _warned;

        public static bool ReceiveReady => _receiveReady;

        public static bool IsRunning
        {
            get
            {
                RefreshRunner();
                return ReadBool(_runner, "IsRunning");
            }
        }

        public static bool IsServer
        {
            get
            {
                RefreshRunner();
                return ReadBool(_runner, "IsServer");
            }
        }

        public static int Tick
        {
            get
            {
                RefreshRunner();
                return ReadInt(ReadMember(_runner, "Tick"));
            }
        }

        public static float TickRate
        {
            get
            {
                RefreshRunner();
                return ReadFloat(ReadMember(_runner, "TickRate"));
            }
        }

        public static int LocalPlayerId
        {
            get
            {
                RefreshRunner();
                return ReadPlayerId(ReadMember(_runner, "LocalPlayer"));
            }
        }

        public static void MarkReceiveReady(bool ready)
        {
            _receiveReady = ready;
            if (ready) Log.Info("Fusion reliable-data receive path is ready");
        }

        public static IEnumerable<int> ActivePlayerIds()
        {
            RefreshRunner();
            if (_runner == null) yield break;

            foreach (var player in Enumerate(ReadMember(_runner, "ActivePlayers")))
            {
                int id = ReadPlayerId(player);
                if (id >= 0) yield return id;
            }
        }

        public static bool ReliableDataPermitted(bool isHost)
        {
            if (!_receiveReady) return false;
            RefreshRunner();
            if (_runner == null) return false;

            try
            {
                var configType = FindFusionType("NetworkProjectConfig");
                var global = ReadStaticMember(configType, "Global");
                var network = ReadMember(global, "Network");
                int modes = ReadInt(ReadMember(network, "ReliableDataTransferModes"));

                // Fusion flags verified for this build: ClientToServer=1 and
                // ClientToClientWithServerProxy=2. The shipped config is 3.
                int required = isHost ? 2 : 1;
                return (modes & required) == required;
            }
            catch (Exception ex)
            {
                WarnOnce("could not verify Fusion ReliableDataTransferModes: " + ex.Message);
                return false;
            }
        }

        public static void SendToServer(int[] keyParts, ArraySegment<byte> payload)
        {
            RefreshRunner();
            if (_runner == null || !_receiveReady) return;

            InvokeSend("SendReliableDataToServer", null, keyParts, payload);
        }

        public static void SendToPlayer(
            int playerId, int[] keyParts, ArraySegment<byte> payload)
        {
            RefreshRunner();
            if (_runner == null || !_receiveReady) return;

            object player = FindActivePlayer(playerId);
            if (player == null) return;
            InvokeSend("SendReliableDataToPlayer", player, keyParts, payload);
        }

        public static void Reset()
        {
            lock (Gate)
            {
                _runner = null;
                _reliableKey = null;
                _receiveReady = false;
                _warned = false;
            }
        }

        private static void RefreshRunner()
        {
            lock (Gate)
            {
                if (_runner != null && ReadBool(_runner, "IsRunning")) return;

                _runner = null;
                _reliableKey = null;
                _runnerType ??= FindFusionType("NetworkRunner");
                if (_runnerType == null) return;

                foreach (var candidate in Enumerate(ReadStaticMember(_runnerType, "Instances")))
                {
                    if (candidate != null && ReadBool(candidate, "IsRunning"))
                    {
                        _runner = candidate;
                        break;
                    }
                }
            }
        }

        private static object FindActivePlayer(int playerId)
        {
            foreach (var player in Enumerate(ReadMember(_runner, "ActivePlayers")))
                if (ReadPlayerId(player) == playerId) return player;
            return null;
        }

        private static void InvokeSend(
            string methodName,
            object player,
            int[] keyParts,
            ArraySegment<byte> payload)
        {
            var methods = _runner.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => m.Name == methodName)
                .OrderBy(m => m.GetParameters().Length)
                .ToArray();

            foreach (var method in methods)
            {
                var parameters = method.GetParameters();
                int expected = player == null ? 2 : 3;
                if (parameters.Length != expected) continue;

                try
                {
                    int offset = player == null ? 0 : 1;
                    var arguments = new object[parameters.Length];
                    if (player != null) arguments[0] = player;
                    arguments[offset] = GetReliableKey(keyParts, parameters[offset].ParameterType);
                    arguments[offset + 1] = ConvertPayload(payload, parameters[offset + 1].ParameterType);
                    method.Invoke(_runner, arguments);
                    return;
                }
                catch (Exception ex)
                {
                    WarnOnce(methodName + " failed: " + Unwrap(ex).Message);
                    // Generated Fusion wrappers can expose multiple overload projections.
                    // Keep looking, but remain fail-closed if none accepts our payload.
                }
            }

            WarnOnce("Fusion method not found: " + methodName);
        }

        private static object GetReliableKey(int[] parts, Type expectedType)
        {
            if (_reliableKey != null && expectedType.IsInstanceOfType(_reliableKey))
                return _reliableKey;

            if (parts == null || parts.Length != 4)
                throw new ArgumentException("reliable key requires four integers", nameof(parts));

            var keyType = expectedType ?? FindFusionType("Sockets.ReliableKey");
            var factory = keyType?.GetMethod(
                "FromInts", BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(int), typeof(int), typeof(int), typeof(int) },
                modifiers: null);
            if (factory == null) throw new MissingMethodException("ReliableKey.FromInts");

            _reliableKey = factory.Invoke(null, new object[] { parts[0], parts[1], parts[2], parts[3] });
            return _reliableKey;
        }

        private static object ConvertPayload(ArraySegment<byte> segment, Type expectedType)
        {
            var bytes = new byte[segment.Count];
            if (segment.Array != null && segment.Count > 0)
                Buffer.BlockCopy(segment.Array, segment.Offset, bytes, 0, segment.Count);

            if (expectedType == typeof(byte[]) || expectedType.IsInstanceOfType(bytes)) return bytes;
            if (expectedType == typeof(ArraySegment<byte>)) return new ArraySegment<byte>(bytes);

            var byteArrayCtor = expectedType.GetConstructor(new[] { typeof(byte[]) });
            if (byteArrayCtor != null) return byteArrayCtor.Invoke(new object[] { bytes });

            var lengthCtor = expectedType.GetConstructor(new[] { typeof(long) }) ??
                             expectedType.GetConstructor(new[] { typeof(int) });
            if (lengthCtor == null)
                throw new InvalidCastException("cannot create " + expectedType.FullName + " from byte[]");

            object array = lengthCtor.GetParameters()[0].ParameterType == typeof(long)
                ? lengthCtor.Invoke(new object[] { (long)bytes.Length })
                : lengthCtor.Invoke(new object[] { bytes.Length });
            var item = expectedType.GetProperty("Item");
            if (item == null || !item.CanWrite)
                throw new InvalidCastException(expectedType.FullName + " has no writable indexer");
            for (int i = 0; i < bytes.Length; i++) item.SetValue(array, bytes[i], new object[] { i });
            return array;
        }

        private static int ReadPlayerId(object player)
        {
            if (player == null) return -1;
            object value = ReadMember(player, "PlayerId") ?? ReadMember(player, "RawEncoded");
            return value == null ? ReadInt(player) : ReadInt(value);
        }

        private static object ReadStaticMember(Type type, string name)
        {
            if (type == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            return type.GetProperty(name, flags)?.GetValue(null) ?? type.GetField(name, flags)?.GetValue(null);
        }

        private static object ReadMember(object instance, string name)
        {
            if (instance == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var type = instance.GetType();
            return type.GetProperty(name, flags)?.GetValue(instance) ?? type.GetField(name, flags)?.GetValue(instance);
        }

        private static bool ReadBool(object instance, string name)
        {
            try { return Convert.ToBoolean(ReadMember(instance, name)); }
            catch { return false; }
        }

        private static int ReadInt(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt32(value); }
            catch
            {
                foreach (string name in new[] { "Raw", "Value", "_tick", "_value", "_index" })
                {
                    object nested = ReadMember(value, name);
                    if (nested == null || ReferenceEquals(nested, value)) continue;
                    try { return Convert.ToInt32(nested); } catch { }
                }
                return 0;
            }
        }

        private static float ReadFloat(object value)
        {
            try { return Convert.ToSingle(value); }
            catch { return 0f; }
        }

        private static IEnumerable<object> Enumerate(object collection)
        {
            if (collection == null) yield break;

            if (collection is IEnumerable managed)
            {
                foreach (var item in managed) yield return item;
                yield break;
            }

            // Generated Il2CppSystem collections do not implement the managed
            // System.Collections.IEnumerable interface. Prefer Count + indexer where
            // available (NetworkRunner.Instances), then use their projected enumerator
            // (ActivePlayers). Everything stays reflection-only and fail-closed.
            object countValue = ReadMember(collection, "Count");
            if (countValue == null)
            {
                object countView = ProjectIl2CppInterface(
                    collection, "Il2CppSystem.Collections.Generic.IReadOnlyCollection`1");
                countValue = ReadMember(countView, "Count");
            }
            var itemProperty = collection.GetType().GetProperty(
                "Item", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (countValue != null && itemProperty != null && itemProperty.CanRead)
            {
                int count = Math.Max(0, ReadInt(countValue));
                for (int i = 0; i < count; i++)
                {
                    object item;
                    try { item = itemProperty.GetValue(collection, new object[] { i }); }
                    catch { yield break; }
                    yield return item;
                }
                yield break;
            }

            object enumerator;
            try
            {
                var getEnumerator = collection.GetType().GetMethod(
                    "GetEnumerator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                enumerator = getEnumerator?.Invoke(collection, null);
            }
            catch { yield break; }

            if (enumerator == null) yield break;
            var enumeratorType = enumerator.GetType();
            var moveNext = enumeratorType.GetMethod(
                "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            object moveNextTarget = enumerator;
            if (moveNext == null)
            {
                moveNextTarget = ProjectIl2CppInterface(
                    enumerator, "Il2CppSystem.Collections.IEnumerator");
                moveNext = moveNextTarget?.GetType().GetMethod(
                    "MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
            }
            var current = enumeratorType.GetProperty(
                "Current", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext == null || current == null) yield break;

            while (true)
            {
                bool hasNext;
                try { hasNext = Convert.ToBoolean(moveNext.Invoke(moveNextTarget, null)); }
                catch { yield break; }
                if (!hasNext) yield break;

                object item;
                try { item = current.GetValue(enumerator); }
                catch { yield break; }
                yield return item;
            }
        }

        private static object ProjectIl2CppInterface(object instance, string fullName)
        {
            if (instance == null) return null;

            try
            {
                var projectedType = FindType(fullName);
                if (projectedType == null) return null;
                if (projectedType.ContainsGenericParameters)
                {
                    var arguments = instance.GetType().GetGenericArguments();
                    if (arguments.Length != projectedType.GetGenericArguments().Length) return null;
                    projectedType = projectedType.MakeGenericType(arguments);
                }

                object pointerValue = ReadMember(instance, "Pointer");
                if (pointerValue is not IntPtr pointer || pointer == IntPtr.Zero) return null;
                var ctor = projectedType.GetConstructor(new[] { typeof(IntPtr) });
                return ctor?.Invoke(new object[] { pointer });
            }
            catch { return null; }
        }

        private static Type FindFusionType(string relativeName) =>
            FindType("Il2CppFusion." + relativeName) ?? FindType("Fusion." + relativeName);

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
            ex is TargetInvocationException { InnerException: not null } tie ? tie.InnerException : ex;

        private static void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            Log.Warn(message + "; synchronized playback remains disabled");
        }
    }
}

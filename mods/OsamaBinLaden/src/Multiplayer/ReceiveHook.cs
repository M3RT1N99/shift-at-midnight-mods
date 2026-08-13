using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace OsamaBinLaden.Multiplayer
{
    /// <summary>Forwards only this mod's Fusion reliable-data key into its transport.</summary>
    internal static class ReceiveHook
    {
        private const string HarmonyId = "io.github.m3rt1n99.osamabinladen.receive";

        private static FusionTransport _transport;
        private static HarmonyLib.Harmony _harmony;
        private static bool _applied;

        public static bool Applied => _applied;

        public static bool Apply(FusionTransport transport)
        {
            if (_applied) return ReferenceEquals(_transport, transport);
            if (transport == null) return false;

            try
            {
                Type callbackType = FindType("Il2Cpp.FusionNetworkManager") ??
                                    FindType("FusionNetworkManager");
                MethodInfo target = callbackType?.GetMethods(
                        BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(method =>
                        method.Name == "OnReliableDataReceived" &&
                        method.GetParameters().Length == 4);
                if (target == null) return false;

                _transport = transport;
                _harmony = new HarmonyLib.Harmony(HarmonyId);
                MethodInfo postfix = typeof(ReceiveHook).GetMethod(
                    nameof(Received), BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                _applied = true;
                Log.Info("Fusion receive hook installed for multiplayer encounters");
                return true;
            }
            catch (Exception ex)
            {
                _transport = null;
                _harmony = null;
                _applied = false;
                Log.Warn("Fusion receive hook unavailable: " + Unwrap(ex).Message);
                return false;
            }
        }

        public static void Remove(FusionTransport owner)
        {
            if (!_applied || !ReferenceEquals(_transport, owner)) return;
            try { _harmony?.UnpatchSelf(); }
            catch (Exception ex) { Log.Warn("receive-hook cleanup failed: " + ex.Message); }
            _applied = false;
            _transport = null;
            _harmony = null;
        }

        private static void Received(object[] __args)
        {
            FusionTransport transport = _transport;
            if (transport == null || __args == null || __args.Length < 4) return;

            try
            {
                if (!KeyMatches(__args[2])) return;
                ArraySegment<byte> payload = ToBytes(__args[3]);
                if (payload.Count == 0) return;
                transport.Dispatch(PlayerIdOf(__args[1]), payload);
            }
            catch (Exception ex)
            {
                Log.Error("reliable-data receive failed: " + ex.Message);
            }
        }

        private static bool KeyMatches(object key)
        {
            if (key == null) return false;
            try
            {
                MethodInfo fromInts = key.GetType().GetMethod(
                    "FromInts", BindingFlags.Public | BindingFlags.Static);
                object expected = fromInts?.Invoke(null, new object[]
                {
                    EncounterProtocol.ReliableKey.Part0, EncounterProtocol.ReliableKey.Part1,
                    EncounterProtocol.ReliableKey.Part2, EncounterProtocol.ReliableKey.Part3
                });
                return expected != null && expected.Equals(key);
            }
            catch { return false; }
        }

        private static int PlayerIdOf(object player)
        {
            if (player == null) return -1;
            try
            {
                Type type = player.GetType();
                foreach (string name in new[] { "PlayerId", "RawEncoded", "AsIndex" })
                {
                    PropertyInfo property = type.GetProperty(name);
                    if (property != null) return Convert.ToInt32(property.GetValue(player));
                    FieldInfo field = type.GetField(name);
                    if (field != null) return Convert.ToInt32(field.GetValue(player));
                }
            }
            catch { }
            return -1;
        }

        private static ArraySegment<byte> ToBytes(object data)
        {
            if (data == null) return default;
            if (data is byte[] direct) return new ArraySegment<byte>(direct);
            if (data is ArraySegment<byte> segment) return segment;

            try
            {
                Type type = data.GetType();
                PropertyInfo arrayProperty = type.GetProperty("Array");
                if (arrayProperty != null)
                {
                    object inner = arrayProperty.GetValue(data);
                    int offset = Convert.ToInt32(type.GetProperty("Offset")?.GetValue(data) ?? 0);
                    int count = Convert.ToInt32(type.GetProperty("Count")?.GetValue(data) ?? 0);
                    if (inner is byte[] bytes && count >= 0 && offset >= 0 &&
                        offset <= bytes.Length - count)
                        return new ArraySegment<byte>(bytes, offset, count);
                    if (inner != null) return Copy(inner, offset, count);
                }
                return Copy(data, 0, -1);
            }
            catch { return default; }
        }

        private static ArraySegment<byte> Copy(object source, int offset, int requestedCount)
        {
            if (source == null || offset < 0) return default;
            Type type = source.GetType();
            PropertyInfo lengthProperty = type.GetProperty("Length") ?? type.GetProperty("Count");
            PropertyInfo indexer = type.GetProperty("Item");
            if (lengthProperty != null && indexer != null)
            {
                int length = Convert.ToInt32(lengthProperty.GetValue(source));
                int count = requestedCount < 0 ? length - offset : requestedCount;
                if (count < 0 || offset > length - count || count > 2048) return default;
                var buffer = new byte[count];
                for (int index = 0; index < count; index++)
                    buffer[index] = Convert.ToByte(indexer.GetValue(source, new object[] { offset + index }));
                return new ArraySegment<byte>(buffer);
            }

            if (source is IEnumerable sequence)
            {
                var collected = new System.Collections.Generic.List<byte>();
                foreach (object item in sequence)
                {
                    if (collected.Count >= 2048) return default;
                    collected.Add(Convert.ToByte(item));
                }
                return new ArraySegment<byte>(collected.ToArray());
            }
            return default;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, false, false);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException { InnerException: not null } target
                ? target.InnerException
                : exception;
    }
}

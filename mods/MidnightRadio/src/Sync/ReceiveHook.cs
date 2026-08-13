using System;
using System.Collections;
using System.Linq;
using System.Reflection;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// Delivers Fusion reliable-data frames to the mod.
    ///
    /// Why a Harmony patch and not an INetworkRunnerCallbacks implementation: registering a
    /// managed type that implements an Il2Cpp *interface* means injecting it into the
    /// IL2CPP type system, which is the fragile path - interface method slots, generics and
    /// GC lifetime all have to line up. The game already implements the callbacks in
    /// FusionCallbackBase, and metadata confirms
    ///
    ///     public virtual void OnReliableDataReceived(
    ///         NetworkRunner runner, PlayerRef player, ReliableKey key, &lt;data&gt;)
    ///
    /// is a plain virtual method. Patching that needs no injection at all.
    ///
    /// The data parameter's type is deliberately never named here. Metadata shows the send
    /// and receive sides carrying different type indices for the same concept, so the hook
    /// reads its arguments as object[] and converts by shape. That way an interop naming
    /// change cannot stop the patch from applying.
    ///
    /// Everything is fail-closed: if the patch does not apply, RunnerBridge is never marked
    /// receive-ready, sending stays disabled, and the radio plays locally.
    /// </summary>
    internal static class ReceiveHook
    {
        private const string HarmonyId = "io.github.m3rt1n99.midnightradio.receive";

        private static SyncTransport _transport;
        private static object _harmony;
        private static bool _applied;

        public static bool Applied => _applied;

        /// <summary>Applies the patch. Returns false if the receive path is unavailable.</summary>
        public static bool Apply(SyncTransport transport)
        {
            if (_applied) return true;
            _transport = transport;

            try
            {
                var callbackType = FindType("Il2Cpp.FusionCallbackBase")
                                   ?? FindType("FusionCallbackBase");
                if (callbackType == null)
                {
                    Log.Warn("FusionCallbackBase not found - synced music stays off, "
                             + "local playback is unaffected");
                    return false;
                }

                var target = callbackType.GetMethod(
                    "OnReliableDataReceived",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                if (target == null)
                {
                    Log.Warn("FusionCallbackBase.OnReliableDataReceived not found - "
                             + "synced music stays off");
                    return false;
                }

                var harmonyType = FindType("HarmonyLib.Harmony");
                if (harmonyType == null)
                {
                    Log.Warn("HarmonyLib unavailable - synced music stays off");
                    return false;
                }

                _harmony = Activator.CreateInstance(harmonyType, HarmonyId);

                var postfix = typeof(ReceiveHook).GetMethod(
                    nameof(Received), BindingFlags.NonPublic | BindingFlags.Static);

                var harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
                var wrapped = Activator.CreateInstance(harmonyMethodType, postfix);

                harmonyType.GetMethods()
                    .First(m => m.Name == "Patch" && m.GetParameters().Length >= 3)
                    .Invoke(_harmony, BuildPatchArgs(harmonyType, target, wrapped));

                _applied = true;
                RunnerBridge.MarkReceiveReady(true);
                Log.Info("reliable-data receive hook installed");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("could not install the receive hook (" + Unwrap(ex).Message
                         + ") - synced music stays off, local playback is unaffected");
                return false;
            }
        }

        public static void Remove()
        {
            if (!_applied || _harmony == null) return;
            Log.Guard("ReceiveHook.Remove", () =>
            {
                _harmony.GetType().GetMethod("UnpatchSelf")?.Invoke(_harmony, null);
            });
            _applied = false;
            _transport = null;
            RunnerBridge.MarkReceiveReady(false);
        }

        // Patch(original, prefix, postfix, transpiler, finalizer) - only the postfix slot is
        // filled; the arity differs between Harmony versions, so it is built by length.
        private static object[] BuildPatchArgs(Type harmonyType, MethodInfo target, object postfix)
        {
            var parameters = harmonyType.GetMethods()
                .First(m => m.Name == "Patch" && m.GetParameters().Length >= 3)
                .GetParameters();

            var args = new object[parameters.Length];
            args[0] = target;
            for (int i = 1; i < args.Length; i++) args[i] = null;
            if (args.Length > 2) args[2] = postfix;   // prefix at 1, postfix at 2
            return args;
        }

        /// <summary>
        /// Harmony postfix. Reading arguments as object[] keeps this independent of the
        /// interop types. Never throws - an exception here would surface inside the game's
        /// network callback.
        /// </summary>
        private static void Received(object[] __args)
        {
            if (_transport == null || __args == null || __args.Length < 4) return;

            Log.Guard("reliable-data receive", () =>
            {
                if (!KeyMatches(__args[2])) return;   // not ours; the game has its own traffic

                var payload = ToBytes(__args[3]);
                if (payload.Count == 0) return;

                _transport.Dispatch(PlayerIdOf(__args[1]), payload);
            });
        }

        // ------------------------------------------------------------------ conversions

        /// <summary>
        /// ReliableKey is a 16-byte struct exposing GetInts/GetUlongs. Rather than fight
        /// reflection over out-parameters, an expected key is built with FromInts and
        /// compared for equality.
        /// </summary>
        private static bool KeyMatches(object key)
        {
            if (key == null) return false;

            try
            {
                var keyType = key.GetType();
                var fromInts = keyType.GetMethod("FromInts",
                    BindingFlags.Public | BindingFlags.Static);
                if (fromInts == null) return false;

                var expected = fromInts.Invoke(null, new object[]
                {
                    SyncProtocol.KeyParts[0], SyncProtocol.KeyParts[1],
                    SyncProtocol.KeyParts[2], SyncProtocol.KeyParts[3],
                });
                return expected != null && expected.Equals(key);
            }
            catch
            {
                return false;
            }
        }

        private static int PlayerIdOf(object player)
        {
            if (player == null) return -1;
            try
            {
                var type = player.GetType();
                foreach (var name in new[] { "PlayerId", "RawEncoded", "AsIndex" })
                {
                    var property = type.GetProperty(name);
                    if (property != null) return Convert.ToInt32(property.GetValue(player));
                    var field = type.GetField(name);
                    if (field != null) return Convert.ToInt32(field.GetValue(player));
                }
            }
            catch { }
            return -1;
        }

        /// <summary>
        /// Accepts whatever shape the runtime hands over: an ArraySegment-like value with
        /// Array/Offset/Count, a plain byte array, or any indexable byte collection.
        /// </summary>
        private static ArraySegment<byte> ToBytes(object data)
        {
            if (data == null) return default;

            if (data is byte[] direct) return new ArraySegment<byte>(direct);
            if (data is ArraySegment<byte> segment) return segment;

            try
            {
                var type = data.GetType();

                var arrayMember = type.GetProperty("Array");
                if (arrayMember != null)
                {
                    var inner = arrayMember.GetValue(data);
                    int offset = Convert.ToInt32(type.GetProperty("Offset")?.GetValue(data) ?? 0);
                    int count = Convert.ToInt32(type.GetProperty("Count")?.GetValue(data) ?? 0);

                    if (inner is byte[] bytes && count > 0 && offset + count <= bytes.Length)
                        return new ArraySegment<byte>(bytes, offset, count);
                    if (inner != null) return Copy(inner, offset, count);
                }

                return Copy(data, 0, -1);
            }
            catch
            {
                return default;
            }
        }

        /// <summary>Copies an Il2Cpp array or any enumerable of bytes into managed memory.</summary>
        private static ArraySegment<byte> Copy(object source, int offset, int count)
        {
            var type = source.GetType();

            var lengthMember = type.GetProperty("Length") ?? type.GetProperty("Count");
            if (lengthMember != null)
            {
                int length = Convert.ToInt32(lengthMember.GetValue(source));
                if (count >= 0) length = Math.Min(length, offset + count);

                var indexer = type.GetProperty("Item");
                if (indexer != null)
                {
                    var buffer = new byte[Math.Max(0, length - offset)];
                    for (int i = 0; i < buffer.Length; i++)
                        buffer[i] = Convert.ToByte(
                            indexer.GetValue(source, new object[] { offset + i }));
                    return new ArraySegment<byte>(buffer);
                }
            }

            if (source is IEnumerable sequence)
            {
                var collected = new System.Collections.Generic.List<byte>();
                foreach (var item in sequence) collected.Add(Convert.ToByte(item));
                return new ArraySegment<byte>(collected.ToArray());
            }

            return default;
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

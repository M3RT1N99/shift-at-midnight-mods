using System;

namespace OsamaBinLaden.Multiplayer
{
    /// <summary>
    /// Four stable integers which the transport maps to Fusion's ReliableKey. Keeping the
    /// values here avoids a Fusion dependency in the wire format and in protocol tests.
    /// </summary>
    internal readonly struct ReliableKey4 : IEquatable<ReliableKey4>
    {
        public ReliableKey4(int part0, int part1, int part2, int part3)
        {
            Part0 = part0;
            Part1 = part1;
            Part2 = part2;
            Part3 = part3;
        }

        public int Part0 { get; }
        public int Part1 { get; }
        public int Part2 { get; }
        public int Part3 { get; }

        public bool Equals(ReliableKey4 other) =>
            Part0 == other.Part0 && Part1 == other.Part1 &&
            Part2 == other.Part2 && Part3 == other.Part3;

        public override bool Equals(object value) => value is ReliableKey4 other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Part0, Part1, Part2, Part3);
    }

    internal enum EncounterMessageType : byte
    {
        Hello = 1,
        HelloAck = 2,
        Ready = 3,
        Spawn = 4,
        Detonate = 5,
        Cancel = 6,
        Heartbeat = 7
    }

    internal enum EncounterReason : byte
    {
        None = 0,
        HuntStarted = 1,
        ManualSpawn = 2,
        Reconnected = 3,
        ReachedTarget = 4,
        FuseExpired = 5,
        HuntEnded = 6,
        HostLeft = 7,
        InvalidState = 8,
        LifetimeExpired = 9
    }

    /// <summary>Host-selected settings which make every client simulate the same encounter.</summary>
    internal struct EncounterConfigSnapshot
    {
        public float RunSpeed;
        public float TriggerDistance;
        public float FuseSeconds;
        public float LifetimeSeconds;
        public float VisualScale;
        public float ScreamVolume;
        public float ExplosionRadius;
        public float ExplosionDamage;

        public static EncounterConfigSnapshot SafeDefaults => new EncounterConfigSnapshot
        {
            RunSpeed = 6f,
            TriggerDistance = 1.75f,
            FuseSeconds = 0.35f,
            LifetimeSeconds = 35f,
            VisualScale = 1f,
            ScreamVolume = 0.8f,
            ExplosionRadius = 4f,
            ExplosionDamage = 100f
        };
    }

    /// <summary>
    /// A fixed-size protocol message. HostEpoch, both challenge nonces, Sequence, EncounterId
    /// and HostTick are anti-replay inputs. The session layer must verify echoed challenges and
    /// remember the accepted epoch/sequence/tick. This data is not authentication: the receiver
    /// must compare the Fusion sender with its authoritative host for every host-only message.
    /// </summary>
    internal sealed class EncounterMessage
    {
        public EncounterMessageType Type { get; set; }
        public EncounterReason Reason { get; set; }
        public ulong Sequence { get; set; }
        public ulong HostEpoch { get; set; }
        public ulong ClientNonce { get; set; }
        public ulong HostNonce { get; set; }
        public ulong EncounterId { get; set; }
        public int HostPlayerId { get; set; } = -1;
        public int TargetPlayerId { get; set; } = -1;
        public int TargetPlayerRawEncoded { get; set; }
        public uint TargetNetworkId { get; set; }
        public ulong HostTick { get; set; }
        public float SpawnX { get; set; }
        public float SpawnY { get; set; }
        public float SpawnZ { get; set; }
        public EncounterConfigSnapshot Config { get; set; } = EncounterConfigSnapshot.SafeDefaults;
    }

    /// <summary>
    /// Versioned, bounded, little-endian encounter protocol. It intentionally has no Unity,
    /// MelonLoader or Fusion dependency so malformed network input can be tested in isolation.
    /// </summary>
    internal static class EncounterProtocol
    {
        // Bytes on the wire are 4f 42 4c 4e ("OBLN").
        internal const uint Magic = 0x4e4c424fU;
        internal const ushort Version = 1;
        internal const int PacketSize = 120;
        internal const int HeaderSize = 12;
        internal const int PayloadSize = PacketSize - HeaderSize;

        internal const float MaximumCoordinateMagnitude = 10_000f;
        internal const int MaximumPlayerId = 65_535;
        internal const int MaximumPlayerRawEncoded = MaximumPlayerId + 1;

        // Four non-zero, stable integers. The final word includes protocol major/minor 1.0.
        internal static readonly ReliableKey4 ReliableKey = new ReliableKey4(
            unchecked((int)0x4f424c4e),
            unchecked((int)0x454e434e),
            unchecked((int)0x50524f54),
            unchecked((int)0x00010000));

        internal static bool RequiresHostSender(EncounterMessageType type) =>
            type == EncounterMessageType.HelloAck ||
            type == EncounterMessageType.Spawn ||
            type == EncounterMessageType.Detonate ||
            type == EncounterMessageType.Cancel ||
            type == EncounterMessageType.Heartbeat;

        internal static bool TryEncode(EncounterMessage message, out byte[] packet)
        {
            packet = null;
            if (!IsValid(message)) return false;

            byte[] result = new byte[PacketSize];
            BoundedWriter writer = new BoundedWriter(result);

            bool written =
                writer.TryWriteUInt32(Magic) &&
                writer.TryWriteUInt16(Version) &&
                writer.TryWriteByte((byte)message.Type) &&
                writer.TryWriteByte((byte)message.Reason) &&
                writer.TryWriteUInt16(PayloadSize) &&
                writer.TryWriteUInt16(0) && // Reserved flags; must remain zero in v1.
                writer.TryWriteUInt64(message.Sequence) &&
                writer.TryWriteUInt64(message.HostEpoch) &&
                writer.TryWriteUInt64(message.ClientNonce) &&
                writer.TryWriteUInt64(message.HostNonce) &&
                writer.TryWriteUInt64(message.EncounterId) &&
                writer.TryWriteInt32(message.HostPlayerId) &&
                writer.TryWriteInt32(message.TargetPlayerId) &&
                writer.TryWriteInt32(message.TargetPlayerRawEncoded) &&
                writer.TryWriteUInt32(message.TargetNetworkId) &&
                writer.TryWriteUInt64(message.HostTick) &&
                writer.TryWriteSingle(message.SpawnX) &&
                writer.TryWriteSingle(message.SpawnY) &&
                writer.TryWriteSingle(message.SpawnZ) &&
                writer.TryWriteSingle(message.Config.RunSpeed) &&
                writer.TryWriteSingle(message.Config.TriggerDistance) &&
                writer.TryWriteSingle(message.Config.FuseSeconds) &&
                writer.TryWriteSingle(message.Config.LifetimeSeconds) &&
                writer.TryWriteSingle(message.Config.VisualScale) &&
                writer.TryWriteSingle(message.Config.ScreamVolume) &&
                writer.TryWriteSingle(message.Config.ExplosionRadius) &&
                writer.TryWriteSingle(message.Config.ExplosionDamage);

            if (!written || writer.Position != PacketSize) return false;
            packet = result;
            return true;
        }

        internal static bool TryDecode(byte[] packet, out EncounterMessage message)
        {
            return TryDecode(packet, 0, packet == null ? 0 : packet.Length, out message);
        }

        internal static bool TryDecode(byte[] packet, int offset, int count, out EncounterMessage message)
        {
            message = null;
            if (packet == null || offset < 0 || count != PacketSize || offset > packet.Length - count)
                return false;

            BoundedReader reader = new BoundedReader(packet, offset, count);
            if (!reader.TryReadUInt32(out uint magic) || magic != Magic ||
                !reader.TryReadUInt16(out ushort version) || version != Version ||
                !reader.TryReadByte(out byte rawType) || !IsKnownType(rawType) ||
                !reader.TryReadByte(out byte rawReason) || !IsKnownReason(rawReason) ||
                !reader.TryReadUInt16(out ushort payloadSize) || payloadSize != PayloadSize ||
                !reader.TryReadUInt16(out ushort reserved) || reserved != 0)
            {
                return false;
            }

            EncounterMessage candidate = new EncounterMessage
            {
                Type = (EncounterMessageType)rawType,
                Reason = (EncounterReason)rawReason
            };

            if (!reader.TryReadUInt64(out ulong sequence) ||
                !reader.TryReadUInt64(out ulong hostEpoch) ||
                !reader.TryReadUInt64(out ulong clientNonce) ||
                !reader.TryReadUInt64(out ulong hostNonce) ||
                !reader.TryReadUInt64(out ulong encounterId) ||
                !reader.TryReadInt32(out int hostPlayerId) ||
                !reader.TryReadInt32(out int targetPlayerId) ||
                !reader.TryReadInt32(out int targetPlayerRawEncoded) ||
                !reader.TryReadUInt32(out uint targetNetworkId) ||
                !reader.TryReadUInt64(out ulong hostTick) ||
                !reader.TryReadSingle(out float spawnX) ||
                !reader.TryReadSingle(out float spawnY) ||
                !reader.TryReadSingle(out float spawnZ) ||
                !reader.TryReadSingle(out float runSpeed) ||
                !reader.TryReadSingle(out float triggerDistance) ||
                !reader.TryReadSingle(out float fuseSeconds) ||
                !reader.TryReadSingle(out float lifetimeSeconds) ||
                !reader.TryReadSingle(out float visualScale) ||
                !reader.TryReadSingle(out float screamVolume) ||
                !reader.TryReadSingle(out float explosionRadius) ||
                !reader.TryReadSingle(out float explosionDamage) ||
                reader.Remaining != 0)
            {
                return false;
            }

            candidate.Sequence = sequence;
            candidate.HostEpoch = hostEpoch;
            candidate.ClientNonce = clientNonce;
            candidate.HostNonce = hostNonce;
            candidate.EncounterId = encounterId;
            candidate.HostPlayerId = hostPlayerId;
            candidate.TargetPlayerId = targetPlayerId;
            candidate.TargetPlayerRawEncoded = targetPlayerRawEncoded;
            candidate.TargetNetworkId = targetNetworkId;
            candidate.HostTick = hostTick;
            candidate.SpawnX = spawnX;
            candidate.SpawnY = spawnY;
            candidate.SpawnZ = spawnZ;
            candidate.Config = new EncounterConfigSnapshot
            {
                RunSpeed = runSpeed,
                TriggerDistance = triggerDistance,
                FuseSeconds = fuseSeconds,
                LifetimeSeconds = lifetimeSeconds,
                VisualScale = visualScale,
                ScreamVolume = screamVolume,
                ExplosionRadius = explosionRadius,
                ExplosionDamage = explosionDamage
            };

            if (!IsValid(candidate)) return false;
            message = candidate;
            return true;
        }

        private static bool IsValid(EncounterMessage message)
        {
            if (message == null ||
                !IsKnownType((byte)message.Type) ||
                !IsKnownReason((byte)message.Reason) ||
                message.Sequence == 0 ||
                message.HostPlayerId < -1 || message.HostPlayerId > MaximumPlayerId ||
                message.TargetPlayerId < -1 || message.TargetPlayerId > MaximumPlayerId ||
                message.TargetPlayerRawEncoded < 0 ||
                message.TargetPlayerRawEncoded > MaximumPlayerRawEncoded ||
                !IsCoordinate(message.SpawnX) ||
                !IsCoordinate(message.SpawnY) ||
                !IsCoordinate(message.SpawnZ) ||
                !IsConfigValid(message.Config))
            {
                return false;
            }

            switch (message.Type)
            {
                case EncounterMessageType.Hello:
                    // The initial packet contributes only the client's challenge. Identity is
                    // taken from Fusion's sender metadata, never trusted from this packet.
                    return message.Reason == EncounterReason.None &&
                           message.ClientNonce != 0 &&
                           message.HostEpoch == 0 &&
                           message.HostNonce == 0 &&
                           HasNoEncounterOrTarget(message) &&
                           message.HostPlayerId == -1 &&
                           message.HostTick == 0;

                case EncounterMessageType.HelloAck:
                    // The session layer additionally verifies that ClientNonce equals Hello.
                    return message.Reason == EncounterReason.None &&
                           HasAllChallenges(message) &&
                           HasHostIdentity(message) &&
                           HasNoEncounterOrTarget(message);

                case EncounterMessageType.Ready:
                    // The session layer verifies both echoed nonces before marking this peer ready.
                    return message.Reason == EncounterReason.None &&
                           HasAllChallenges(message) &&
                           HasHostIdentity(message) &&
                           HasNoEncounterOrTarget(message);

                case EncounterMessageType.Spawn:
                case EncounterMessageType.Detonate:
                case EncounterMessageType.Cancel:
                    return HasAllChallenges(message) &&
                           HasHostIdentity(message) &&
                           message.EncounterId != 0 &&
                           HasFullTarget(message);

                case EncounterMessageType.Heartbeat:
                    return message.Reason == EncounterReason.None &&
                           HasAllChallenges(message) &&
                           HasHostIdentity(message) &&
                           HasNoEncounterOrTarget(message);

                default:
                    return false;
            }
        }

        private static bool HasAllChallenges(EncounterMessage message) =>
            message.HostEpoch != 0 && message.ClientNonce != 0 && message.HostNonce != 0;

        private static bool HasHostIdentity(EncounterMessage message) => message.HostPlayerId >= 0;

        private static bool HasFullTarget(EncounterMessage message) =>
            message.TargetPlayerId >= 0 &&
            message.TargetPlayerRawEncoded > 0 &&
            message.TargetNetworkId != 0;

        private static bool HasNoEncounterOrTarget(EncounterMessage message) =>
            message.EncounterId == 0 &&
            message.TargetPlayerId == -1 &&
            message.TargetPlayerRawEncoded == 0 &&
            message.TargetNetworkId == 0;

        private static bool IsConfigValid(EncounterConfigSnapshot config) =>
            IsInRange(config.RunSpeed, 1f, 20f) &&
            IsInRange(config.TriggerDistance, 0.5f, 8f) &&
            IsInRange(config.FuseSeconds, 0f, 5f) &&
            IsInRange(config.LifetimeSeconds, 5f, 180f) &&
            IsInRange(config.VisualScale, 0.5f, 3f) &&
            IsInRange(config.ScreamVolume, 0f, 1f) &&
            IsInRange(config.ExplosionRadius, 1f, 20f) &&
            IsInRange(config.ExplosionDamage, 0f, 500f);

        private static bool IsCoordinate(float value) =>
            IsFinite(value) && Math.Abs(value) <= MaximumCoordinateMagnitude;

        private static bool IsInRange(float value, float minimum, float maximum) =>
            IsFinite(value) && value >= minimum && value <= maximum;

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static bool IsKnownType(byte value) =>
            value >= (byte)EncounterMessageType.Hello &&
            value <= (byte)EncounterMessageType.Heartbeat;

        private static bool IsKnownReason(byte value) =>
            value <= (byte)EncounterReason.LifetimeExpired;

        private sealed class BoundedWriter
        {
            private readonly byte[] _buffer;

            internal BoundedWriter(byte[] buffer)
            {
                _buffer = buffer;
            }

            internal int Position { get; private set; }

            internal bool TryWriteByte(byte value)
            {
                if (!CanWrite(1)) return false;
                _buffer[Position++] = value;
                return true;
            }

            internal bool TryWriteUInt16(ushort value)
            {
                if (!CanWrite(2)) return false;
                _buffer[Position++] = (byte)value;
                _buffer[Position++] = (byte)(value >> 8);
                return true;
            }

            internal bool TryWriteUInt32(uint value)
            {
                if (!CanWrite(4)) return false;
                _buffer[Position++] = (byte)value;
                _buffer[Position++] = (byte)(value >> 8);
                _buffer[Position++] = (byte)(value >> 16);
                _buffer[Position++] = (byte)(value >> 24);
                return true;
            }

            internal bool TryWriteInt32(int value) => TryWriteUInt32(unchecked((uint)value));

            internal bool TryWriteUInt64(ulong value)
            {
                return TryWriteUInt32((uint)value) && TryWriteUInt32((uint)(value >> 32));
            }

            internal bool TryWriteSingle(float value) =>
                TryWriteInt32(BitConverter.SingleToInt32Bits(value));

            private bool CanWrite(int byteCount) =>
                byteCount >= 0 && Position <= _buffer.Length - byteCount;
        }

        private sealed class BoundedReader
        {
            private readonly byte[] _buffer;
            private readonly int _end;
            private int _position;

            internal BoundedReader(byte[] buffer, int offset, int count)
            {
                _buffer = buffer;
                _position = offset;
                _end = offset + count;
            }

            internal int Remaining => _end - _position;

            internal bool TryReadByte(out byte value)
            {
                value = 0;
                if (!CanRead(1)) return false;
                value = _buffer[_position++];
                return true;
            }

            internal bool TryReadUInt16(out ushort value)
            {
                value = 0;
                if (!CanRead(2)) return false;
                value = (ushort)(_buffer[_position] | (_buffer[_position + 1] << 8));
                _position += 2;
                return true;
            }

            internal bool TryReadUInt32(out uint value)
            {
                value = 0;
                if (!CanRead(4)) return false;
                value =
                    _buffer[_position] |
                    ((uint)_buffer[_position + 1] << 8) |
                    ((uint)_buffer[_position + 2] << 16) |
                    ((uint)_buffer[_position + 3] << 24);
                _position += 4;
                return true;
            }

            internal bool TryReadInt32(out int value)
            {
                bool success = TryReadUInt32(out uint raw);
                value = unchecked((int)raw);
                return success;
            }

            internal bool TryReadUInt64(out ulong value)
            {
                value = 0;
                if (!TryReadUInt32(out uint low) || !TryReadUInt32(out uint high)) return false;
                value = low | ((ulong)high << 32);
                return true;
            }

            internal bool TryReadSingle(out float value)
            {
                bool success = TryReadInt32(out int raw);
                value = success ? BitConverter.Int32BitsToSingle(raw) : 0f;
                return success;
            }

            private bool CanRead(int byteCount) =>
                byteCount >= 0 && _position <= _end - byteCount;
        }
    }
}

using System;
using System.Text;

namespace MidnightRadio.Sync
{
    /// <summary>
    /// Wire format for MidnightRadio's co-op messages.
    ///
    /// Topology: host-relay. A client sends to the host with SendReliableDataToServer, the
    /// host re-emits to every other modded peer with SendReliableDataToPlayer. Fusion's
    /// reliable channel is ordered per connection, so the host's receive order becomes a
    /// total order that every peer observes identically. That is the reason for the relay:
    /// with direct client-to-client, two peers can apply two commands in opposite orders.
    ///
    /// Only identifiers cross the wire - a URL or a content hash, never audio data.
    /// Every client obtains the audio itself, the same way everyone opening the same link
    /// would. Bulk transfer is a separate, off-by-default path and is not part of this file.
    /// </summary>
    internal static class SyncProtocol
    {
        /// <summary>Bump on any breaking layout change. Peers with a different major refuse politely.</summary>
        public const byte Version = 1;

        /// <summary>Frame marker so a stray payload on our key is never mistaken for ours.</summary>
        public const ushort Magic = 0x4D52; // "MR"

        /// <summary>
        /// Reliable-data key identifying our traffic. Vanilla clients never see it: we only
        /// ever send to peers that completed the handshake.
        /// </summary>
        public static readonly int[] KeyParts = { 0x4D69646E, 0x69676874, 0x52616469, 0x6F000001 };

        public enum MsgType : byte
        {
            Hello         = 0x01, // presence + protocol version + capabilities
            HelloAck      = 0x02, // host answers with the peer roster
            StateRequest  = 0x03, // late joiner asks for the current state
            StateSnapshot = 0x04, // full playback + queue state
            NowPlaying    = 0x10, // track identity + the tick playback starts at
            Control       = 0x11, // play / pause / resume / stop / seek / skip
            QueueAdd      = 0x20,
            QueueRemove   = 0x21,
            Have          = 0x30, // "I can play this track"
            Need          = 0x31, // "I cannot - reason attached"
        }

        public enum ControlOp : byte
        {
            Play = 0, Pause = 1, Resume = 2, Stop = 3, Seek = 4, SkipNext = 5, SkipPrev = 6
        }

        public enum TrackSource : byte
        {
            LocalFile = 0, // identified by content hash
            Url       = 1, // identified by the URL itself; each peer resolves it locally
        }

        public enum NeedReason : byte
        {
            NotInLibrary = 0, Downloading = 1, NoYtDlp = 2, NoFfmpeg = 3, DownloadFailed = 4,
            DecodeFailed = 5, Declined = 6,
        }

        // ---------------------------------------------------------------- writer

        internal sealed class Writer
        {
            private byte[] _buf;
            private int _pos;

            public Writer(MsgType type, int capacity = 256)
            {
                _buf = new byte[capacity];
                U16(Magic);
                U8(Version);
                U8((byte)type);
            }

            private void Ensure(int extra)
            {
                if (_pos + extra <= _buf.Length) return;
                var grown = new byte[Math.Max(_buf.Length * 2, _pos + extra)];
                Buffer.BlockCopy(_buf, 0, grown, 0, _pos);
                _buf = grown;
            }

            public Writer U8(byte v)   { Ensure(1); _buf[_pos++] = v; return this; }
            public Writer Bool(bool v) => U8(v ? (byte)1 : (byte)0);

            public Writer U16(ushort v)
            {
                Ensure(2);
                _buf[_pos++] = (byte)(v & 0xFF);
                _buf[_pos++] = (byte)(v >> 8);
                return this;
            }

            public Writer I32(int v)
            {
                Ensure(4);
                _buf[_pos++] = (byte)(v & 0xFF);
                _buf[_pos++] = (byte)((v >> 8) & 0xFF);
                _buf[_pos++] = (byte)((v >> 16) & 0xFF);
                _buf[_pos++] = (byte)((v >> 24) & 0xFF);
                return this;
            }

            public Writer F32(float v)
            {
                var b = BitConverter.GetBytes(v);
                Ensure(4);
                for (int i = 0; i < 4; i++) _buf[_pos++] = b[i];
                return this;
            }

            /// <summary>Length-prefixed UTF-8, capped so a malicious peer cannot balloon a frame.</summary>
            public Writer Str(string s, int maxBytes = 2048)
            {
                if (string.IsNullOrEmpty(s)) return U16(0);
                int limit = Math.Min(Math.Max(maxBytes, 0), ushort.MaxValue);
                if (limit == 0) return U16(0);

                byte[] bytes;
                int byteCount = Encoding.UTF8.GetByteCount(s);
                if (byteCount <= limit)
                {
                    bytes = Encoding.UTF8.GetBytes(s);
                }
                else
                {
                    // Encoder.Convert stops before a code point that does not fit. A raw
                    // Array.Resize could cut a multi-byte character and put invalid UTF-8
                    // on the wire. The U16 prefix also makes 65535 the absolute ceiling.
                    bytes = new byte[limit];
                    Encoding.UTF8.GetEncoder().Convert(
                        s.AsSpan(), bytes.AsSpan(), true,
                        out _, out int bytesUsed, out _);
                    if (bytesUsed != bytes.Length) Array.Resize(ref bytes, bytesUsed);
                }

                U16((ushort)bytes.Length);
                Ensure(bytes.Length);
                Buffer.BlockCopy(bytes, 0, _buf, _pos, bytes.Length);
                _pos += bytes.Length;
                return this;
            }

            public ArraySegment<byte> Done() => new ArraySegment<byte>(_buf, 0, _pos);
        }

        // ---------------------------------------------------------------- reader

        internal sealed class Reader
        {
            private readonly byte[] _buf;
            private readonly int _end;
            private int _pos;

            public MsgType Type { get; private set; }
            public byte PeerVersion { get; private set; }
            public bool Ok { get; private set; }

            public Reader(ArraySegment<byte> seg)
            {
                _buf = seg.Array;
                _pos = seg.Offset;
                _end = seg.Offset + seg.Count;

                if (Remaining < 4) { Ok = false; return; }
                if (U16() != Magic) { Ok = false; return; }

                PeerVersion = U8();
                Type = (MsgType)U8();
                Ok = true;
            }

            private int Remaining => _end - _pos;

            public byte U8()
            {
                if (Remaining < 1) { Ok = false; return 0; }
                return _buf[_pos++];
            }

            public bool Bool() => U8() != 0;

            public ushort U16()
            {
                if (Remaining < 2) { Ok = false; return 0; }
                return (ushort)(_buf[_pos++] | (_buf[_pos++] << 8));
            }

            public int I32()
            {
                if (Remaining < 4) { Ok = false; return 0; }
                return _buf[_pos++] | (_buf[_pos++] << 8) | (_buf[_pos++] << 16) | (_buf[_pos++] << 24);
            }

            public float F32()
            {
                if (Remaining < 4) { Ok = false; return 0f; }
                var v = BitConverter.ToSingle(_buf, _pos);
                _pos += 4;
                return v;
            }

            public string Str()
            {
                int len = U16();
                if (!Ok || len == 0) return string.Empty;
                if (Remaining < len) { Ok = false; return string.Empty; }
                var s = Encoding.UTF8.GetString(_buf, _pos, len);
                _pos += len;
                return s;
            }
        }

        // ---------------------------------------------------------------- frames

        /// <summary>
        /// Announces the track and the tick at which playback begins. Peers schedule the
        /// start rather than starting immediately, which gives everyone time to load and
        /// makes "together" mean the same instant instead of the same message.
        /// </summary>
        public static ArraySegment<byte> NowPlaying(
            TrackSource source, string trackId, string title,
            int startTick, float startOffsetSec, float durationSec, int stateRev)
        {
            return new Writer(MsgType.NowPlaying, 512)
                .U8((byte)source)
                .Str(trackId)
                .Str(title, 256)
                .I32(startTick)
                .F32(startOffsetSec)
                .F32(durationSec)
                .I32(stateRev)
                .Done();
        }

        public static ArraySegment<byte> Control(ControlOp op, int atTick, float positionSec, int stateRev)
        {
            return new Writer(MsgType.Control, 32)
                .U8((byte)op)
                .I32(atTick)
                .F32(positionSec)
                .I32(stateRev)
                .Done();
        }

        public static ArraySegment<byte> Hello(string displayName, bool canFetchUrls)
        {
            return new Writer(MsgType.Hello, 128)
                .Str(displayName, 64)
                .Bool(canFetchUrls)
                .Done();
        }

        public static ArraySegment<byte> Need(string trackId, NeedReason reason)
        {
            return new Writer(MsgType.Need, 256)
                .Str(trackId)
                .U8((byte)reason)
                .Done();
        }

        public static ArraySegment<byte> Have(string trackId)
        {
            return new Writer(MsgType.Have, 256).Str(trackId).Done();
        }

        /// <summary>
        /// Adds a track to the shared queue. Anyone may send this - the open-queue model is
        /// the point. playNow asks for it to start immediately instead of being queued.
        /// </summary>
        public static ArraySegment<byte> QueueAdd(string trackId, string title, bool playNow)
        {
            return new Writer(MsgType.QueueAdd, 512)
                .Str(trackId)
                .Str(title, 256)
                .Bool(playNow)
                .Done();
        }

        /// <summary>Asks the host for the current state. Sent once on joining.</summary>
        public static ArraySegment<byte> StateRequest()
        {
            return new Writer(MsgType.StateRequest, 8).Done();
        }
    }
}

using System;
using System.Text;

namespace WgSharp.Proto
{
    /// <summary>
    /// WireGuard wire-format constants and message offsets.
    /// Construction = Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s.
    /// </summary>
    public static class Messages
    {
        public const byte TypeInitiation = 1;
        public const byte TypeResponse = 2;
        public const byte TypeCookieReply = 3;
        public const byte TypeTransport = 4;

        public const int InitiationSize = 148;
        public const int ResponseSize = 92;
        public const int CookieReplySize = 64;
        public const int TransportHeaderSize = 16; // type+reserved(4) + receiver(4) + counter(8)

        // Hashed-once protocol labels.
        public const string Construction = "Noise_IKpsk2_25519_ChaChaPoly_BLAKE2s";
        public const string Identifier = "WireGuard v1 zx2c4 Jason@zx2c4.com";
        public const string LabelMac1 = "mac1----";
        public const string LabelCookie = "cookie--";

        public static byte[] Utf8(string s) { return Encoding.ASCII.GetBytes(s); }

        // ---- Initiation field offsets (msg type 1) ----
        // [0]      type
        // [1..3]   reserved (zero)
        // [4..7]   sender index (LE)
        // [8..39]  unencrypted ephemeral
        // [40..87] encrypted static (32 + 16 tag)
        // [88..115]encrypted timestamp (12 + 16 tag)
        // [116..131] mac1
        // [132..147] mac2
        public const int Init_Sender = 4;
        public const int Init_Ephemeral = 8;
        public const int Init_EncStatic = 40;
        public const int Init_EncTimestamp = 88;
        public const int Init_Mac1 = 116;
        public const int Init_Mac2 = 132;

        // ---- Response field offsets (msg type 2) ----
        // [0]      type
        // [1..3]   reserved
        // [4..7]   sender index (LE)
        // [8..11]  receiver index (LE)
        // [12..43] unencrypted ephemeral
        // [44..59] encrypted empty (0 + 16 tag)
        // [60..75] mac1
        // [76..91] mac2
        public const int Resp_Sender = 4;
        public const int Resp_Receiver = 8;
        public const int Resp_Ephemeral = 12;
        public const int Resp_EncEmpty = 44;
        public const int Resp_Mac1 = 60;
        public const int Resp_Mac2 = 76;

        // ---- Transport header (msg type 4) ----
        // [0]      type
        // [1..3]   reserved
        // [4..7]   receiver index (LE)
        // [8..15]  counter (LE 64-bit)
        // [16..]   encrypted packet (+16 tag)
        public const int Tr_Receiver = 4;
        public const int Tr_Counter = 8;
        public const int Tr_Payload = 16;

        // ---- Cookie reply (msg type 3, 64 bytes) ----
        // [0]      type (3)
        // [1..3]   reserved
        // [4..7]   receiver index (LE) — our sender index from the init
        // [8..31]  nonce (24 bytes, for XChaCha20-Poly1305)
        // [32..63] encrypted cookie (16 cookie + 16 tag)
        public const int Cookie_Receiver = 4;
        public const int Cookie_Nonce = 8;
        public const int Cookie_EncCookie = 32;

        public static void WriteLE32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }
        public static uint ReadLE32(byte[] b, int o)
        {
            return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        }
        public static void WriteLE64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }
        public static ulong ReadLE64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }
    }
}

using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// TAI64N timestamp (12 bytes): 8-byte big-endian seconds since 1970 plus the
    /// TAI64 offset (2^62), then 4-byte big-endian nanoseconds. WireGuard embeds
    /// this in the handshake initiation so a responder can reject replayed inits
    /// (it keeps the greatest timestamp seen per peer).
    /// </summary>
    public static class Tai64N
    {
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static byte[] Now()
        {
            DateTime utc = DateTime.UtcNow;
            long ticks = (utc - Epoch).Ticks;       // 100-ns ticks since unix epoch
            long seconds = ticks / 10000000L;
            long nanos = (ticks % 10000000L) * 100L; // remainder -> nanoseconds

            ulong tai = 0x4000000000000000UL + (ulong)seconds; // TAI64 label
            byte[] outp = new byte[12];
            for (int i = 0; i < 8; i++) outp[i] = (byte)(tai >> (56 - 8 * i));   // big-endian
            uint n = (uint)nanos;
            outp[8] = (byte)(n >> 24);
            outp[9] = (byte)(n >> 16);
            outp[10] = (byte)(n >> 8);
            outp[11] = (byte)n;
            return outp;
        }

        /// <summary>Lexicographic compare; a &gt; b means a is newer. Returns &gt;0, 0, or &lt;0.</summary>
        public static int Compare(byte[] a, byte[] b)
        {
            for (int i = 0; i < 12; i++)
            {
                if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
            }
            return 0;
        }
    }
}

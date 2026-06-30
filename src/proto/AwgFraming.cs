using System;
using System.Security.Cryptography;

namespace WgSharp.Proto
{
    /// <summary>
    /// AmneziaWG ("AWG") wire-format support, isolated entirely at the UDP
    /// send/receive boundary. This is a deliberate design choice: Handshake.cs
    /// and Session.cs — the actual Noise_IKpsk2 handshake and transport
    /// crypto — are completely unmodified by AWG support and remain
    /// byte-for-byte standard WireGuard. AwgFraming only:
    ///
    ///   - OUTGOING: takes a standard WireGuard message that Handshake/Session
    ///     already built (type byte 1/2/3/4 at offset 0, 3 zero reserved
    ///     bytes), and disguises it for the wire — replacing those 4 header
    ///     bytes with the config's H1-H4 value and, for Initiation/Response,
    ///     prepending S1/S2 random junk bytes. Also generates the Jc random
    ///     junk packets sent before a handshake initiation.
    ///
    ///   - INCOMING: takes whatever arrived on the UDP socket and, if it
    ///     matches one of the three message shapes a client can receive
    ///     (Response/CookieReply/Transport) against the config's H values and
    ///     S2 padding, strips the disguise and rewrites the header back to
    ///     the standard byte — producing exactly the bytes Handshake.
    ///     ConsumeResponse/ConsumeCookieReply/Session.Decrypt already expect,
    ///     unchanged. Anything that doesn't match is reported as junk/noise
    ///     and the caller drops it.
    ///
    /// Both sides of a tunnel (this client and the server) must be configured
    /// with IDENTICAL Jc/Jmin/Jmax/S1/S2/H1-H4 values — there's no
    /// negotiation, the same way the static keys themselves aren't negotiated.
    /// A standard (non-AWG) WireGuard server can't speak this at all; per-
    /// tunnel use of AwgFraming is gated entirely on Config.IsAmneziaWg.
    /// </summary>
    public static class AwgFraming
    {
        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        /// <summary>Fills <paramref name="buf"/> with cryptographically random bytes.</summary>
        private static byte[] RandomBytes(int len)
        {
            if (len <= 0) return EmptyArray;
            byte[] b = new byte[len];
            Rng.GetBytes(b);
            return b;
        }
        private static readonly byte[] EmptyArray = new byte[0];

        /// <summary>
        /// Builds the Jc junk packets to send before a handshake initiation
        /// attempt, each a random size in [Jmin, Jmax]. Pure noise — no
        /// relation to the real handshake message; an AWG-aware server (or a
        /// standard one, for that matter) just discards anything that
        /// doesn't parse as a real message, so these are never decoded by
        /// either side, only sent to perturb the traffic's size/timing
        /// signature on the wire before the real handshake packet appears.
        /// </summary>
        public static byte[][] BuildJunkPackets(int jc, int jmin, int jmax)
        {
            if (jc <= 0) return new byte[0][];
            if (jmax < jmin) jmax = jmin; // defensive; Config validation should already guarantee this
            var packets = new byte[jc][];
            for (int i = 0; i < jc; i++)
            {
                int size = jmin == jmax ? jmin : jmin + RandomInt(jmax - jmin + 1);
                packets[i] = RandomBytes(size);
            }
            return packets;
        }

        private static int RandomInt(int exclusiveMax)
        {
            if (exclusiveMax <= 1) return 0;
            byte[] b = new byte[4];
            Rng.GetBytes(b);
            uint v = (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
            return (int)(v % (uint)exclusiveMax);
        }

        /// <summary>
        /// Disguises an outgoing handshake INITIATION message (built normally
        /// by Handshake.CreateInitiation: standard[0]=1, standard[1..3]=0) for
        /// the wire: overwrites the 4-byte header with H1, and prepends S1
        /// random bytes. Returns a NEW array; does not mutate the input.
        /// </summary>
        public static byte[] WrapInitiation(byte[] standard, uint h1, int s1)
        {
            byte[] junk = RandomBytes(s1);
            byte[] outBuf = new byte[junk.Length + standard.Length];
            Array.Copy(junk, 0, outBuf, 0, junk.Length);
            Array.Copy(standard, 0, outBuf, junk.Length, standard.Length);
            Messages.WriteLE32(outBuf, junk.Length, h1);
            return outBuf;
        }

        /// <summary>
        /// Disguises an outgoing TRANSPORT message (built normally by
        /// Session.Encrypt: standard[0]=4, standard[1..3]=0) for the wire:
        /// overwrites just the 4-byte header with H4. No S-byte padding is
        /// used for transport messages in AWG. Mutates and returns the SAME
        /// array (no copy needed — transport messages are already freshly
        /// allocated per-packet by Session.Encrypt, so this is safe and
        /// avoids an extra allocation on the hot data path).
        /// </summary>
        public static byte[] WrapTransport(byte[] standard, uint h4)
        {
            Messages.WriteLE32(standard, 0, h4);
            return standard;
        }

        public enum InboundKind { Unknown, Response, CookieReply, Transport }

        /// <summary>
        /// Examines a raw datagram just pulled off the UDP socket and, if it
        /// matches the shape of a Response, CookieReply, or Transport message
        /// under this config's H values (and S2 padding, for Response),
        /// strips any S-junk prefix and rewrites the header back to the
        /// standard byte (2/3/4 respectively) — so the returned bytes are
        /// exactly what Handshake.ConsumeResponse / the cookie-reply path /
        /// Session.Decrypt already expect, unmodified from how they handle
        /// standard WireGuard. Returns InboundKind.Unknown (with a null
        /// translated buffer) if nothing matches — likely one of our own Jc
        /// junk packets being reflected by some middlebox, or genuine noise;
        /// the caller should just drop it.
        /// </summary>
        public static InboundKind TranslateInbound(byte[] raw, int length, uint h2, uint h3, uint h4, int s2,
            out byte[] translated)
        {
            translated = null;

            // Cookie reply: fixed size, no S-byte padding in AWG.
            if (length == Messages.CookieReplySize && length >= 4 &&
                Messages.ReadLE32(raw, 0) == h3)
            {
                translated = Slice(raw, 0, length);
                Messages.WriteLE32(translated, 0, Messages.TypeCookieReply);
                return InboundKind.CookieReply;
            }

            // Response: fixed size AFTER stripping S2 bytes of padding.
            int respTotal = s2 + Messages.ResponseSize;
            if (length == respTotal && length - s2 >= 4 &&
                Messages.ReadLE32(raw, s2) == h2)
            {
                translated = Slice(raw, s2, Messages.ResponseSize);
                Messages.WriteLE32(translated, 0, Messages.TypeResponse);
                return InboundKind.Response;
            }

            // Transport: variable size (header + ciphertext), no S-byte
            // padding in AWG; just the header magic differs.
            if (length >= Messages.TransportHeaderSize + 16 &&
                Messages.ReadLE32(raw, 0) == h4)
            {
                translated = Slice(raw, 0, length);
                Messages.WriteLE32(translated, 0, Messages.TypeTransport);
                return InboundKind.Transport;
            }

            return InboundKind.Unknown;
        }

        private static byte[] Slice(byte[] src, int offset, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, offset, r, 0, len);
            return r;
        }
    }
}

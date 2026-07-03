using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD (RFC 8439). WireGuard's only cipher: handshake
    /// fields and transport data. Nonce is 96-bit; WireGuard supplies a 64-bit
    /// counter in the low 8 bytes of a 12-byte little-endian nonce (4 zero bytes
    /// first). This implementation takes the full 12-byte nonce.
    ///
    /// PERFORMANCE NOTES (hot data path when the CNG backend is unavailable):
    ///   - All per-block scratch (ChaCha state, keystream, Poly1305 one-time key,
    ///     length block) lives in a [ThreadStatic] bundle, created once per
    ///     thread. The original implementation allocated two uint[16] arrays per
    ///     64-byte ChaCha block (~44 allocations for one 1400-byte packet) plus a
    ///     fully materialized Poly1305 MAC buffer per packet; steady-state this
    ///     now allocates nothing.
    ///   - Poly1305 runs as a streaming MAC (Poly1305Stream): the AEAD MAC input
    ///     (aad || pad || ct || pad || lengths) is fed in pieces instead of being
    ///     concatenated into a temporary buffer.
    ///   - EncryptInto/DecryptInto operate at caller-supplied offsets so
    ///     Session can encrypt straight into the outgoing transport message and
    ///     decrypt straight out of the receive buffer, with no intermediate
    ///     ciphertext/plaintext arrays.
    /// The legacy whole-array Encrypt/Decrypt/XEncrypt/XDecrypt APIs are kept as
    /// thin wrappers (the handshake uses them; it is a cold path).
    /// </summary>
    public static class ChaCha20Poly1305
    {
        // ---------------- per-thread scratch ----------------
        // One bundle per thread: the transport path touches this from the
        // tunnel's outbound/keepalive/inbound threads, the handshake from
        // whichever thread runs it. ThreadStatic keeps them isolated with no
        // locking. Nothing here is secret-lifetime-critical beyond a single
        // call: otk is overwritten by the next operation on the same thread.
        private sealed class Scratch
        {
            public readonly uint[] S = new uint[16];      // ChaCha input state
            public readonly uint[] Block = new uint[16];  // ChaCha working/output state
            public readonly byte[] Ks = new byte[64];     // one block of keystream
            public readonly byte[] Otk = new byte[32];    // Poly1305 one-time key
            public readonly byte[] LenBlock = new byte[16]; // MAC trailer (lengths)
            public readonly Poly1305Stream Poly = new Poly1305Stream();
        }

        [ThreadStatic] private static Scratch _scratch;
        private static Scratch GetScratch()
        {
            Scratch s = _scratch;
            if (s == null) { s = new Scratch(); _scratch = s; }
            return s;
        }

        private static readonly byte[] ZeroPad = new byte[16]; // shared all-zero pad source

        // ---------------- ChaCha20 block function ----------------
        private static uint Rotl(uint x, int n) { return (x << n) | (x >> (32 - n)); }

        private static void QuarterRound(uint[] s, int a, int b, int c, int d)
        {
            s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 16);
            s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 12);
            s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 8);
            s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 7);
        }

        // Computes one ChaCha20 block into w. s is caller-provided scratch for
        // the input state (so this function allocates nothing).
        private static void ChachaBlock(uint[] w, uint[] s, byte[] key, uint counter, byte[] nonce)
        {
            s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) s[4 + i] = LE32(key, i * 4);
            s[12] = counter;
            s[13] = LE32(nonce, 0);
            s[14] = LE32(nonce, 4);
            s[15] = LE32(nonce, 8);

            Array.Copy(s, w, 16);
            for (int i = 0; i < 10; i++)
            {
                QuarterRound(w, 0, 4, 8, 12);
                QuarterRound(w, 1, 5, 9, 13);
                QuarterRound(w, 2, 6, 10, 14);
                QuarterRound(w, 3, 7, 11, 15);
                QuarterRound(w, 0, 5, 10, 15);
                QuarterRound(w, 1, 6, 11, 12);
                QuarterRound(w, 2, 7, 8, 13);
                QuarterRound(w, 3, 4, 9, 14);
            }
            for (int i = 0; i < 16; i++) w[i] += s[i];
        }

        private static uint LE32(byte[] b, int o)
        {
            return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        }

        // HChaCha20: derives a 32-byte subkey from a 32-byte key and a 16-byte
        // nonce. Same round function as ChaCha20 but with NO final addition; the
        // output is words 0..3 and 12..15. Cold path (cookie replies only).
        private static byte[] HChaCha20(byte[] key, byte[] nonce16)
        {
            uint[] w = new uint[16];
            w[0] = 0x61707865; w[1] = 0x3320646e; w[2] = 0x79622d32; w[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) w[4 + i] = LE32(key, i * 4);
            w[12] = LE32(nonce16, 0);
            w[13] = LE32(nonce16, 4);
            w[14] = LE32(nonce16, 8);
            w[15] = LE32(nonce16, 12);

            for (int i = 0; i < 10; i++)
            {
                QuarterRound(w, 0, 4, 8, 12);
                QuarterRound(w, 1, 5, 9, 13);
                QuarterRound(w, 2, 6, 10, 14);
                QuarterRound(w, 3, 7, 11, 15);
                QuarterRound(w, 0, 5, 10, 15);
                QuarterRound(w, 1, 6, 11, 12);
                QuarterRound(w, 2, 7, 8, 13);
                QuarterRound(w, 3, 4, 9, 14);
            }

            byte[] outKey = new byte[32];
            int[] idx = { 0, 1, 2, 3, 12, 13, 14, 15 };
            for (int i = 0; i < 8; i++)
            {
                uint v = w[idx[i]];
                outKey[i * 4] = (byte)v;
                outKey[i * 4 + 1] = (byte)(v >> 8);
                outKey[i * 4 + 2] = (byte)(v >> 16);
                outKey[i * 4 + 3] = (byte)(v >> 24);
            }
            return outKey;
        }

        /// <summary>
        /// XChaCha20-Poly1305 decrypt with a 24-byte nonce, used by WireGuard cookie
        /// replies. Derives a subkey via HChaCha20(key, nonce[0..15]) and runs
        /// ChaCha20-Poly1305 with nonce = 0x00000000 || nonce[16..23].
        /// </summary>
        public static byte[] XDecrypt(byte[] key, byte[] nonce24, byte[] ciphertext, byte[] aad)
        {
            byte[] subKey = HChaCha20(key, Slice(nonce24, 0, 16));
            byte[] nonce12 = new byte[12];
            Array.Copy(nonce24, 16, nonce12, 4, 8);
            return Decrypt(subKey, nonce12, ciphertext, aad);
        }

        public static byte[] XEncrypt(byte[] key, byte[] nonce24, byte[] plaintext, byte[] aad)
        {
            byte[] subKey = HChaCha20(key, Slice(nonce24, 0, 16));
            byte[] nonce12 = new byte[12];
            Array.Copy(nonce24, 16, nonce12, 4, 8);
            return Encrypt(subKey, nonce12, plaintext, aad);
        }

        private static byte[] Slice(byte[] src, int off, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, off, r, 0, len);
            return r;
        }

        // XOR len bytes of input into output using the ChaCha20 keystream
        // starting at the given block counter. Uses the thread scratch bundle;
        // allocates nothing.
        private static void ChaCha20Xor(Scratch sc, byte[] key, uint counter, byte[] nonce,
                                        byte[] input, int inOff, byte[] output, int outOff, int len)
        {
            uint[] block = sc.Block;
            byte[] ks = sc.Ks;
            int pos = 0;
            while (pos < len)
            {
                ChachaBlock(block, sc.S, key, counter, nonce);
                for (int i = 0; i < 16; i++)
                {
                    ks[i * 4] = (byte)block[i];
                    ks[i * 4 + 1] = (byte)(block[i] >> 8);
                    ks[i * 4 + 2] = (byte)(block[i] >> 16);
                    ks[i * 4 + 3] = (byte)(block[i] >> 24);
                }
                int chunk = Math.Min(64, len - pos);
                for (int i = 0; i < chunk; i++)
                    output[outOff + pos + i] = (byte)(input[inOff + pos + i] ^ ks[i]);
                pos += chunk;
                counter++;
            }
        }

        // ---------------- Poly1305 (streaming) ----------------
        // Same 26-bit-limb arithmetic as the original one-shot implementation,
        // restructured so data can be fed in pieces. Reset(key) clamps r and
        // precomputes s-multiples; Update processes full 16-byte blocks,
        // buffering any partial tail; FinishInto handles the final (possibly
        // partial) block, the carry chain, reduction, and adds the key's s half.
        internal sealed class Poly1305Stream
        {
            private uint r0, r1, r2, r3, r4;
            private uint s1, s2, s3, s4;
            private uint h0, h1, h2, h3, h4;
            private uint k16, k20, k24, k28;   // key bytes 16..31 as LE words
            private readonly byte[] _buf = new byte[16];
            private int _bufLen;

            public void Reset(byte[] oneTimeKey)
            {
                r0 = LE32(oneTimeKey, 0) & 0x3ffffff;
                r1 = (LE32(oneTimeKey, 3) >> 2) & 0x3ffff03;
                r2 = (LE32(oneTimeKey, 6) >> 4) & 0x3ffc0ff;
                r3 = (LE32(oneTimeKey, 9) >> 6) & 0x3f03fff;
                r4 = (LE32(oneTimeKey, 12) >> 8) & 0x00fffff;
                s1 = r1 * 5; s2 = r2 * 5; s3 = r3 * 5; s4 = r4 * 5;
                h0 = h1 = h2 = h3 = h4 = 0;
                k16 = LE32(oneTimeKey, 16);
                k20 = LE32(oneTimeKey, 20);
                k24 = LE32(oneTimeKey, 24);
                k28 = LE32(oneTimeKey, 28);
                _bufLen = 0;
            }

            public void Update(byte[] data, int offset, int length)
            {
                // Fill any partial block first.
                if (_bufLen > 0)
                {
                    int need = 16 - _bufLen;
                    int take = Math.Min(need, length);
                    Array.Copy(data, offset, _buf, _bufLen, take);
                    _bufLen += take;
                    offset += take;
                    length -= take;
                    if (_bufLen == 16)
                    {
                        ProcessBlock(_buf, 0, 1u << 24);
                        _bufLen = 0;
                    }
                }
                // Whole blocks straight from the input.
                while (length >= 16)
                {
                    ProcessBlock(data, offset, 1u << 24);
                    offset += 16;
                    length -= 16;
                }
                // Stash the tail.
                if (length > 0)
                {
                    Array.Copy(data, offset, _buf, 0, length);
                    _bufLen = length;
                }
            }

            // Processes one 16-byte block at data[offset]. hibit is 1<<24 for a
            // full block, 0 for the padded final partial block (whose 0x01
            // terminator is already in the buffer bytes).
            private void ProcessBlock(byte[] data, int offset, uint hibit)
            {
                uint t0 = LE32(data, offset);
                uint t1 = LE32(data, offset + 4);
                uint t2 = LE32(data, offset + 8);
                uint t3 = LE32(data, offset + 12);

                h0 += t0 & 0x3ffffff;
                h1 += ((t0 >> 26) | (t1 << 6)) & 0x3ffffff;
                h2 += ((t1 >> 20) | (t2 << 12)) & 0x3ffffff;
                h3 += ((t2 >> 14) | (t3 << 18)) & 0x3ffffff;
                h4 += (t3 >> 8) | hibit;

                ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
                ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
                ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
                ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
                ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

                ulong c;
                c = d0 >> 26; h0 = (uint)d0 & 0x3ffffff; d1 += c;
                c = d1 >> 26; h1 = (uint)d1 & 0x3ffffff; d2 += c;
                c = d2 >> 26; h2 = (uint)d2 & 0x3ffffff; d3 += c;
                c = d3 >> 26; h3 = (uint)d3 & 0x3ffffff; d4 += c;
                c = d4 >> 26; h4 = (uint)d4 & 0x3ffffff; h0 += (uint)c * 5;
                c = h0 >> 26; h0 &= 0x3ffffff; h1 += (uint)c;
            }

            /// <summary>Writes the 16-byte tag to tag[tagOff..].</summary>
            public void FinishInto(byte[] tag, int tagOff)
            {
                // Final partial block (if any): append 0x01, zero-fill, hibit=0.
                if (_bufLen > 0)
                {
                    _buf[_bufLen] = 1;
                    for (int i = _bufLen + 1; i < 16; i++) _buf[i] = 0;
                    ProcessBlock(_buf, 0, 0);
                    _bufLen = 0;
                }

                // fully carry h
                uint cc;
                cc = h1 >> 26; h1 &= 0x3ffffff; h2 += cc;
                cc = h2 >> 26; h2 &= 0x3ffffff; h3 += cc;
                cc = h3 >> 26; h3 &= 0x3ffffff; h4 += cc;
                cc = h4 >> 26; h4 &= 0x3ffffff; h0 += cc * 5;
                cc = h0 >> 26; h0 &= 0x3ffffff; h1 += cc;

                // compute h - p
                uint g0 = h0 + 5; cc = g0 >> 26; g0 &= 0x3ffffff;
                uint g1 = h1 + cc; cc = g1 >> 26; g1 &= 0x3ffffff;
                uint g2 = h2 + cc; cc = g2 >> 26; g2 &= 0x3ffffff;
                uint g3 = h3 + cc; cc = g3 >> 26; g3 &= 0x3ffffff;
                uint g4 = h4 + cc - (1u << 26);

                // select h if h < p, else g = h - p  (constant-time mask).
                uint mask = 0u - (g4 >> 31);
                h0 = (h0 & mask) | (g0 & ~mask);
                h1 = (h1 & mask) | (g1 & ~mask);
                h2 = (h2 & mask) | (g2 & ~mask);
                h3 = (h3 & mask) | (g3 & ~mask);
                h4 = (h4 & mask) | (g4 & ~mask);

                // serialize h to 128-bit little endian, add s (key bytes 16..31)
                ulong f0 = ((ulong)h0 | (ulong)h1 << 26) & 0xffffffff;
                ulong f1 = ((ulong)h1 >> 6 | (ulong)h2 << 20) & 0xffffffff;
                ulong f2 = ((ulong)h2 >> 12 | (ulong)h3 << 14) & 0xffffffff;
                ulong f3 = ((ulong)h3 >> 18 | (ulong)h4 << 8) & 0xffffffff;

                ulong acc;
                acc = f0 + k16; WriteLE(tag, tagOff, (uint)acc); acc >>= 32;
                acc += f1 + k20; WriteLE(tag, tagOff + 4, (uint)acc); acc >>= 32;
                acc += f2 + k24; WriteLE(tag, tagOff + 8, (uint)acc); acc >>= 32;
                acc += f3 + k28; WriteLE(tag, tagOff + 12, (uint)acc);
            }

            private static uint LE32(byte[] b, int o)
            {
                return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
            }
            private static void WriteLE(byte[] b, int o, uint v)
            {
                b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
            }
        }

        private static void WriteLE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static void WriteLE64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        // Generates the Poly1305 one-time key into sc.Otk (block 0 keystream).
        private static void PolyKeyGen(Scratch sc, byte[] key, byte[] nonce)
        {
            ChachaBlock(sc.Block, sc.S, key, 0, nonce);
            for (int i = 0; i < 8; i++) WriteLE(sc.Otk, i * 4, sc.Block[i]);
        }

        // Feeds the RFC 8439 AEAD MAC input (aad || pad16 || ct || pad16 ||
        // le64(aadLen) || le64(ctLen)) into the streaming Poly1305 and writes
        // the tag. No intermediate buffer is materialized.
        private static void ComputeMacInto(Scratch sc, byte[] aad,
                                           byte[] ct, int ctOff, int ctLen,
                                           byte[] tag, int tagOff)
        {
            Poly1305Stream poly = sc.Poly;
            poly.Reset(sc.Otk);

            int aadLen = aad == null ? 0 : aad.Length;
            if (aadLen > 0)
            {
                poly.Update(aad, 0, aadLen);
                int pad = (16 - (aadLen % 16)) % 16;
                if (pad > 0) poly.Update(ZeroPad, 0, pad);
            }
            if (ctLen > 0)
            {
                poly.Update(ct, ctOff, ctLen);
                int pad = (16 - (ctLen % 16)) % 16;
                if (pad > 0) poly.Update(ZeroPad, 0, pad);
            }
            WriteLE64(sc.LenBlock, 0, (ulong)aadLen);
            WriteLE64(sc.LenBlock, 8, (ulong)ctLen);
            poly.Update(sc.LenBlock, 0, 16);
            poly.FinishInto(tag, tagOff);
        }

        /// <summary>
        /// Encrypt plaintext[ptOff..ptOff+ptLen) into outBuf[outOff..] as
        /// ciphertext || 16-byte tag (ptLen + 16 bytes written). outBuf may not
        /// alias plaintext over the written range. Allocation-free after the
        /// first call on a thread.
        /// </summary>
        public static void EncryptInto(byte[] key, byte[] nonce12,
                                       byte[] plaintext, int ptOff, int ptLen,
                                       byte[] aad, byte[] outBuf, int outOff)
        {
            Scratch sc = GetScratch();
            PolyKeyGen(sc, key, nonce12);
            ChaCha20Xor(sc, key, 1, nonce12, plaintext, ptOff, outBuf, outOff, ptLen);
            ComputeMacInto(sc, aad, outBuf, outOff, ptLen, outBuf, outOff + ptLen);
        }

        /// <summary>
        /// Decrypt input[inOff..inOff+inLen) (ciphertext || 16-byte tag) into
        /// outBuf[outOff..] (inLen - 16 bytes written). Returns false on tag
        /// mismatch, in which case nothing is written to outBuf. Allocation-free
        /// after the first call on a thread.
        /// </summary>
        public static bool DecryptInto(byte[] key, byte[] nonce12,
                                       byte[] input, int inOff, int inLen,
                                       byte[] aad, byte[] outBuf, int outOff)
        {
            if (inLen < 16) return false;
            int ctLen = inLen - 16;
            Scratch sc = GetScratch();
            PolyKeyGen(sc, key, nonce12);
            ComputeMacInto(sc, aad, input, inOff, ctLen, sc.Ks, 0); // tag into scratch (first 16 of Ks)

            int diff = 0;
            for (int i = 0; i < 16; i++) diff |= sc.Ks[i] ^ input[inOff + ctLen + i];
            if (diff != 0) return false; // constant-time tag compare

            ChaCha20Xor(sc, key, 1, nonce12, input, inOff, outBuf, outOff, ctLen);
            return true;
        }

        /// <summary>Encrypt: returns ciphertext || 16-byte tag.</summary>
        public static byte[] Encrypt(byte[] key, byte[] nonce12, byte[] plaintext, byte[] aad)
        {
            byte[] ct = new byte[plaintext.Length + 16];
            EncryptInto(key, nonce12, plaintext, 0, plaintext.Length, aad, ct, 0);
            return ct;
        }

        /// <summary>Decrypt ciphertext||tag. Returns plaintext, or null if tag invalid.</summary>
        public static byte[] Decrypt(byte[] key, byte[] nonce12, byte[] ciphertext, byte[] aad)
        {
            if (ciphertext.Length < 16) return null;
            byte[] pt = new byte[ciphertext.Length - 16];
            if (!DecryptInto(key, nonce12, ciphertext, 0, ciphertext.Length, aad, pt, 0)) return null;
            return pt;
        }

        /// <summary>WireGuard nonce: 64-bit counter, little-endian, in bytes 4..11.</summary>
        public static byte[] NonceFromCounter(ulong counter)
        {
            byte[] n = new byte[12];
            WriteLE64(n, 4, counter);
            return n;
        }

        /// <summary>Write the WireGuard nonce for a counter into an existing 12-byte buffer.</summary>
        public static void NonceFromCounterInto(ulong counter, byte[] nonce12)
        {
            nonce12[0] = 0; nonce12[1] = 0; nonce12[2] = 0; nonce12[3] = 0;
            WriteLE64(nonce12, 4, counter);
        }
    }
}

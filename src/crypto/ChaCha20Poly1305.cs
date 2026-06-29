using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// ChaCha20-Poly1305 AEAD (RFC 8439). WireGuard's only cipher: handshake
    /// fields and transport data. Nonce is 96-bit; WireGuard supplies a 64-bit
    /// counter in the low 8 bytes of a 12-byte little-endian nonce (4 zero bytes
    /// first). This implementation takes the full 12-byte nonce.
    /// </summary>
    public static class ChaCha20Poly1305
    {
        // ---------------- ChaCha20 block function ----------------
        private static uint Rotl(uint x, int n) { return (x << n) | (x >> (32 - n)); }

        private static void QuarterRound(uint[] s, int a, int b, int c, int d)
        {
            s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 16);
            s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 12);
            s[a] += s[b]; s[d] = Rotl(s[d] ^ s[a], 8);
            s[c] += s[d]; s[b] = Rotl(s[b] ^ s[c], 7);
        }

        private static void ChachaBlock(uint[] outState, byte[] key, uint counter, byte[] nonce)
        {
            uint[] s = new uint[16];
            s[0] = 0x61707865; s[1] = 0x3320646e; s[2] = 0x79622d32; s[3] = 0x6b206574;
            for (int i = 0; i < 8; i++) s[4 + i] = LE32(key, i * 4);
            s[12] = counter;
            s[13] = LE32(nonce, 0);
            s[14] = LE32(nonce, 4);
            s[15] = LE32(nonce, 8);

            uint[] w = new uint[16];
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
            for (int i = 0; i < 16; i++) outState[i] = w[i] + s[i];
        }

        private static uint LE32(byte[] b, int o)
        {
            return (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        }

        // HChaCha20: derives a 32-byte subkey from a 32-byte key and a 16-byte
        // nonce. Same round function as ChaCha20 but with NO final addition; the
        // output is words 0..3 and 12..15.
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

        private static void ChaCha20Xor(byte[] key, uint counter, byte[] nonce,
                                        byte[] input, int inOff, byte[] output, int outOff, int len)
        {
            uint[] block = new uint[16];
            byte[] ks = new byte[64];
            int pos = 0;
            while (pos < len)
            {
                ChachaBlock(block, key, counter, nonce);
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

        // ---------------- Poly1305 ----------------
        // Computes the 16-byte tag over `data` using the 32-byte one-time key.
        private static byte[] Poly1305(byte[] oneTimeKey, byte[] data, int dataLen)
        {
            // r is clamped; arithmetic mod 2^130 - 5 done with 26-bit limbs.
            uint r0 = LE32(oneTimeKey, 0) & 0x3ffffff;
            uint r1 = (LE32(oneTimeKey, 3) >> 2) & 0x3ffff03;
            uint r2 = (LE32(oneTimeKey, 6) >> 4) & 0x3ffc0ff;
            uint r3 = (LE32(oneTimeKey, 9) >> 6) & 0x3f03fff;
            uint r4 = (LE32(oneTimeKey, 12) >> 8) & 0x00fffff;

            uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;

            uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;

            int offset = 0;
            int remaining = dataLen;
            byte[] blk = new byte[16];
            while (remaining > 0)
            {
                int chunk = Math.Min(16, remaining);
                for (int i = 0; i < 16; i++) blk[i] = 0;
                Array.Copy(data, offset, blk, 0, chunk);
                // append the 1 bit
                uint hibit;
                if (chunk == 16) { hibit = 1u << 24; }
                else { blk[chunk] = 1; hibit = 0; }

                uint t0 = LE32(blk, 0);
                uint t1 = LE32(blk, 4);
                uint t2 = LE32(blk, 8);
                uint t3 = LE32(blk, 12);

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

                offset += chunk;
                remaining -= chunk;
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
            // g4's high bit is set exactly when h < p (the top-limb subtract
            // underflowed), so mask = all-ones in that case and we keep h.
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

            byte[] tag = new byte[16];
            ulong acc;
            acc = f0 + LE32(oneTimeKey, 16); WriteLE(tag, 0, (uint)acc); acc >>= 32;
            acc += f1 + LE32(oneTimeKey, 20); WriteLE(tag, 4, (uint)acc); acc >>= 32;
            acc += f2 + LE32(oneTimeKey, 24); WriteLE(tag, 8, (uint)acc); acc >>= 32;
            acc += f3 + LE32(oneTimeKey, 28); WriteLE(tag, 12, (uint)acc);
            return tag;
        }

        private static void WriteLE(byte[] b, int o, uint v)
        {
            b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); b[o + 2] = (byte)(v >> 16); b[o + 3] = (byte)(v >> 24);
        }

        private static byte[] PolyKeyGen(byte[] key, byte[] nonce)
        {
            uint[] block = new uint[16];
            ChachaBlock(block, key, 0, nonce);
            byte[] otk = new byte[32];
            for (int i = 0; i < 8; i++) WriteLE(otk, i * 4, block[i]);
            return otk;
        }

        private static byte[] BuildMac(byte[] aad, byte[] ct, int ctLen)
        {
            int aadLen = aad == null ? 0 : aad.Length;
            int aadPad = (16 - (aadLen % 16)) % 16;
            int ctPad = (16 - (ctLen % 16)) % 16;
            byte[] m = new byte[aadLen + aadPad + ctLen + ctPad + 16];
            int p = 0;
            if (aadLen > 0) { Array.Copy(aad, 0, m, p, aadLen); p += aadLen; }
            p += aadPad;
            Array.Copy(ct, 0, m, p, ctLen); p += ctLen;
            p += ctPad;
            // lengths as 64-bit LE
            WriteLE64(m, p, (ulong)aadLen); p += 8;
            WriteLE64(m, p, (ulong)ctLen);
            return m;
        }

        private static void WriteLE64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        /// <summary>Encrypt: returns ciphertext || 16-byte tag.</summary>
        public static byte[] Encrypt(byte[] key, byte[] nonce12, byte[] plaintext, byte[] aad)
        {
            byte[] otk = PolyKeyGen(key, nonce12);
            byte[] ct = new byte[plaintext.Length + 16];
            ChaCha20Xor(key, 1, nonce12, plaintext, 0, ct, 0, plaintext.Length);
            byte[] mac = BuildMac(aad, ct, plaintext.Length);
            byte[] tag = Poly1305(otk, mac, mac.Length);
            Array.Copy(tag, 0, ct, plaintext.Length, 16);
            return ct;
        }

        /// <summary>Decrypt ciphertext||tag. Returns plaintext, or null if tag invalid.</summary>
        public static byte[] Decrypt(byte[] key, byte[] nonce12, byte[] ciphertext, byte[] aad)
        {
            if (ciphertext.Length < 16) return null;
            int ctLen = ciphertext.Length - 16;
            byte[] otk = PolyKeyGen(key, nonce12);
            byte[] mac = BuildMac(aad, ciphertext, ctLen);
            byte[] tag = Poly1305(otk, mac, mac.Length);

            int diff = 0;
            for (int i = 0; i < 16; i++) diff |= tag[i] ^ ciphertext[ctLen + i];
            if (diff != 0) return null; // constant-time tag compare

            byte[] pt = new byte[ctLen];
            ChaCha20Xor(key, 1, nonce12, ciphertext, 0, pt, 0, ctLen);
            return pt;
        }

        /// <summary>WireGuard nonce: 64-bit counter, little-endian, in bytes 4..11.</summary>
        public static byte[] NonceFromCounter(ulong counter)
        {
            byte[] n = new byte[12];
            WriteLE64(n, 4, counter);
            return n;
        }
    }
}

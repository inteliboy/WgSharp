using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// X25519 (RFC 7748). Montgomery-ladder scalar multiplication over
    /// Curve25519, using 64-bit limb field arithmetic (radix 2^25.5, ref10-style).
    /// Provides the DH used throughout the WireGuard handshake.
    /// </summary>
    public static class Curve25519
    {
        public const int KeySize = 32;

        /// <summary>Compute the public key for a secret (clamped X25519 of base point 9).</summary>
        public static byte[] ScalarMultBase(byte[] secret)
        {
            byte[] basePoint = new byte[32];
            basePoint[0] = 9;
            return ScalarMult(secret, basePoint);
        }

        /// <summary>X25519(secret, u): clamps secret, returns the 32-byte shared value.</summary>
        public static byte[] ScalarMult(byte[] secret, byte[] uCoord)
        {
            byte[] e = new byte[32];
            Array.Copy(secret, e, 32);
            e[0] &= 248;
            e[31] &= 127;
            e[31] |= 64;

            long[] x1 = FieldFromBytes(uCoord);
            long[] x2 = FieldOne();
            long[] z2 = FieldZero();
            long[] x3 = FieldCopy(x1);
            long[] z3 = FieldOne();

            int swap = 0;
            for (int pos = 254; pos >= 0; pos--)
            {
                int bit = (e[pos >> 3] >> (pos & 7)) & 1;
                swap ^= bit;
                CSwap(swap, x2, x3);
                CSwap(swap, z2, z3);
                swap = bit;

                long[] a = FieldAdd(x2, z2);
                long[] aa = FieldSquare(a);
                long[] b = FieldSub(x2, z2);
                long[] bb = FieldSquare(b);
                long[] e_ = FieldSub(aa, bb);
                long[] c = FieldAdd(x3, z3);
                long[] d = FieldSub(x3, z3);
                long[] da = FieldMul(d, a);
                long[] cb = FieldMul(c, b);

                long[] t0 = FieldAdd(da, cb);
                x3 = FieldSquare(t0);
                long[] t1 = FieldSub(da, cb);
                long[] t1s = FieldSquare(t1);
                z3 = FieldMul(t1s, x1);

                x2 = FieldMul(aa, bb);
                long[] t2 = FieldMul121666(e_);
                long[] t3 = FieldAdd(bb, t2);
                z2 = FieldMul(e_, t3);
            }

            CSwap(swap, x2, x3);
            CSwap(swap, z2, z3);

            long[] zinv = FieldInvert(z2);
            long[] res = FieldMul(x2, zinv);
            return FieldToBytes(res);
        }

        // ---------------- field arithmetic (10 limbs, ref10 layout) ----------------
        // Limbs alternate 26/25 bits: x = sum h[i] * 2^ceil(25.5*i).

        private static long[] FieldZero() { return new long[10]; }
        private static long[] FieldOne() { var f = new long[10]; f[0] = 1; return f; }
        private static long[] FieldCopy(long[] a) { var f = new long[10]; Array.Copy(a, f, 10); return f; }

        private static long[] FieldAdd(long[] a, long[] b)
        {
            var f = new long[10];
            for (int i = 0; i < 10; i++) f[i] = a[i] + b[i];
            return f;
        }

        private static long[] FieldSub(long[] a, long[] b)
        {
            var f = new long[10];
            for (int i = 0; i < 10; i++) f[i] = a[i] - b[i];
            return f;
        }

        private static long Load3(byte[] b, int o)
        {
            return (long)b[o] | (long)b[o + 1] << 8 | (long)b[o + 2] << 16;
        }
        private static long Load4(byte[] b, int o)
        {
            return (long)b[o] | (long)b[o + 1] << 8 | (long)b[o + 2] << 16 | (long)b[o + 3] << 24;
        }

        private static long[] FieldFromBytes(byte[] s)
        {
            long h0 = Load4(s, 0);
            long h1 = Load3(s, 4) << 6;
            long h2 = Load3(s, 7) << 5;
            long h3 = Load3(s, 10) << 3;
            long h4 = Load3(s, 13) << 2;
            long h5 = Load4(s, 16);
            long h6 = Load3(s, 20) << 7;
            long h7 = Load3(s, 23) << 5;
            long h8 = Load3(s, 26) << 4;
            long h9 = (Load3(s, 29) & 0x7fffff) << 2;

            long c;
            c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
            c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
            c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
            c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
            c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
            c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
            c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
            c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
            c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
            c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;

            return new long[] { h0, h1, h2, h3, h4, h5, h6, h7, h8, h9 };
        }

        private static byte[] FieldToBytes(long[] h)
        {
            h = FieldCopy(h);
            long q, c;
            q = (19 * h[9] + (1L << 24)) >> 25;
            q = (h[0] + q) >> 26;
            q = (h[1] + q) >> 25;
            q = (h[2] + q) >> 26;
            q = (h[3] + q) >> 25;
            q = (h[4] + q) >> 26;
            q = (h[5] + q) >> 25;
            q = (h[6] + q) >> 26;
            q = (h[7] + q) >> 25;
            q = (h[8] + q) >> 26;
            q = (h[9] + q) >> 25;

            h[0] += 19 * q;

            c = h[0] >> 26; h[1] += c; h[0] -= c << 26;
            c = h[1] >> 25; h[2] += c; h[1] -= c << 25;
            c = h[2] >> 26; h[3] += c; h[2] -= c << 26;
            c = h[3] >> 25; h[4] += c; h[3] -= c << 25;
            c = h[4] >> 26; h[5] += c; h[4] -= c << 26;
            c = h[5] >> 25; h[6] += c; h[5] -= c << 25;
            c = h[6] >> 26; h[7] += c; h[6] -= c << 26;
            c = h[7] >> 25; h[8] += c; h[7] -= c << 25;
            c = h[8] >> 26; h[9] += c; h[8] -= c << 26;
            c = h[9] >> 25; h[9] -= c << 25;

            byte[] s = new byte[32];
            s[0] = (byte)h[0];
            s[1] = (byte)(h[0] >> 8);
            s[2] = (byte)(h[0] >> 16);
            s[3] = (byte)((h[0] >> 24) | (h[1] << 2));
            s[4] = (byte)(h[1] >> 6);
            s[5] = (byte)(h[1] >> 14);
            s[6] = (byte)((h[1] >> 22) | (h[2] << 3));
            s[7] = (byte)(h[2] >> 5);
            s[8] = (byte)(h[2] >> 13);
            s[9] = (byte)((h[2] >> 21) | (h[3] << 5));
            s[10] = (byte)(h[3] >> 3);
            s[11] = (byte)(h[3] >> 11);
            s[12] = (byte)((h[3] >> 19) | (h[4] << 6));
            s[13] = (byte)(h[4] >> 2);
            s[14] = (byte)(h[4] >> 10);
            s[15] = (byte)(h[4] >> 18);
            s[16] = (byte)h[5];
            s[17] = (byte)(h[5] >> 8);
            s[18] = (byte)(h[5] >> 16);
            s[19] = (byte)((h[5] >> 24) | (h[6] << 1));
            s[20] = (byte)(h[6] >> 7);
            s[21] = (byte)(h[6] >> 15);
            s[22] = (byte)((h[6] >> 23) | (h[7] << 3));
            s[23] = (byte)(h[7] >> 5);
            s[24] = (byte)(h[7] >> 13);
            s[25] = (byte)((h[7] >> 21) | (h[8] << 4));
            s[26] = (byte)(h[8] >> 4);
            s[27] = (byte)(h[8] >> 12);
            s[28] = (byte)((h[8] >> 20) | (h[9] << 6));
            s[29] = (byte)(h[9] >> 2);
            s[30] = (byte)(h[9] >> 10);
            s[31] = (byte)(h[9] >> 18);
            return s;
        }

        private static void CSwap(int swap, long[] a, long[] b)
        {
            long mask = -(long)swap;
            for (int i = 0; i < 10; i++)
            {
                long x = mask & (a[i] ^ b[i]);
                a[i] ^= x;
                b[i] ^= x;
            }
        }

        private static long[] FieldMul(long[] f, long[] g)
        {
            long f0 = f[0], f1 = f[1], f2 = f[2], f3 = f[3], f4 = f[4],
                 f5 = f[5], f6 = f[6], f7 = f[7], f8 = f[8], f9 = f[9];
            long g0 = g[0], g1 = g[1], g2 = g[2], g3 = g[3], g4 = g[4],
                 g5 = g[5], g6 = g[6], g7 = g[7], g8 = g[8], g9 = g[9];

            long g1_19 = 19 * g1, g2_19 = 19 * g2, g3_19 = 19 * g3, g4_19 = 19 * g4,
                 g5_19 = 19 * g5, g6_19 = 19 * g6, g7_19 = 19 * g7, g8_19 = 19 * g8, g9_19 = 19 * g9;
            long f1_2 = 2 * f1, f3_2 = 2 * f3, f5_2 = 2 * f5, f7_2 = 2 * f7, f9_2 = 2 * f9;

            long h0 = f0*g0 + f1_2*g9_19 + f2*g8_19 + f3_2*g7_19 + f4*g6_19 + f5_2*g5_19 + f6*g4_19 + f7_2*g3_19 + f8*g2_19 + f9_2*g1_19;
            long h1 = f0*g1 + f1*g0 + f2*g9_19 + f3*g8_19 + f4*g7_19 + f5*g6_19 + f6*g5_19 + f7*g4_19 + f8*g3_19 + f9*g2_19;
            long h2 = f0*g2 + f1_2*g1 + f2*g0 + f3_2*g9_19 + f4*g8_19 + f5_2*g7_19 + f6*g6_19 + f7_2*g5_19 + f8*g4_19 + f9_2*g3_19;
            long h3 = f0*g3 + f1*g2 + f2*g1 + f3*g0 + f4*g9_19 + f5*g8_19 + f6*g7_19 + f7*g6_19 + f8*g5_19 + f9*g4_19;
            long h4 = f0*g4 + f1_2*g3 + f2*g2 + f3_2*g1 + f4*g0 + f5_2*g9_19 + f6*g8_19 + f7_2*g7_19 + f8*g6_19 + f9_2*g5_19;
            long h5 = f0*g5 + f1*g4 + f2*g3 + f3*g2 + f4*g1 + f5*g0 + f6*g9_19 + f7*g8_19 + f8*g7_19 + f9*g6_19;
            long h6 = f0*g6 + f1_2*g5 + f2*g4 + f3_2*g3 + f4*g2 + f5_2*g1 + f6*g0 + f7_2*g9_19 + f8*g8_19 + f9_2*g7_19;
            long h7 = f0*g7 + f1*g6 + f2*g5 + f3*g4 + f4*g3 + f5*g2 + f6*g1 + f7*g0 + f8*g9_19 + f9*g8_19;
            long h8 = f0*g8 + f1_2*g7 + f2*g6 + f3_2*g5 + f4*g4 + f5_2*g3 + f6*g2 + f7_2*g1 + f8*g0 + f9_2*g9_19;
            long h9 = f0*g9 + f1*g8 + f2*g7 + f3*g6 + f4*g5 + f5*g4 + f6*g3 + f7*g2 + f8*g1 + f9*g0;

            return Reduce(h0, h1, h2, h3, h4, h5, h6, h7, h8, h9);
        }

        private static long[] FieldSquare(long[] f)
        {
            return FieldMul(f, f); // a clean square is an optimization; correctness first.
        }

        private static long[] FieldMul121666(long[] f)
        {
            long[] h = new long[10];
            for (int i = 0; i < 10; i++) h[i] = f[i] * 121666;
            return Reduce(h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], h[8], h[9]);
        }

        private static long[] Reduce(long h0, long h1, long h2, long h3, long h4,
                                     long h5, long h6, long h7, long h8, long h9)
        {
            long c;
            c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
            c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
            c = (h1 + (1L << 24)) >> 25; h2 += c; h1 -= c << 25;
            c = (h5 + (1L << 24)) >> 25; h6 += c; h5 -= c << 25;
            c = (h2 + (1L << 25)) >> 26; h3 += c; h2 -= c << 26;
            c = (h6 + (1L << 25)) >> 26; h7 += c; h6 -= c << 26;
            c = (h3 + (1L << 24)) >> 25; h4 += c; h3 -= c << 25;
            c = (h7 + (1L << 24)) >> 25; h8 += c; h7 -= c << 25;
            c = (h4 + (1L << 25)) >> 26; h5 += c; h4 -= c << 26;
            c = (h8 + (1L << 25)) >> 26; h9 += c; h8 -= c << 26;
            c = (h9 + (1L << 24)) >> 25; h0 += c * 19; h9 -= c << 25;
            c = (h0 + (1L << 25)) >> 26; h1 += c; h0 -= c << 26;
            return new long[] { h0, h1, h2, h3, h4, h5, h6, h7, h8, h9 };
        }

        private static long[] FieldInvert(long[] z)
        {
            // Fermat: z^(p-2) = z^(2^255 - 21). Standard ref10 addition chain.
            long[] t0 = FieldSquare(z);
            long[] t1 = FieldSquare(t0); t1 = FieldSquare(t1);
            t1 = FieldMul(z, t1);
            t0 = FieldMul(t0, t1);
            long[] t2 = FieldSquare(t0);
            t1 = FieldMul(t1, t2);
            t2 = FieldSquare(t1);
            for (int i = 1; i < 5; i++) t2 = FieldSquare(t2);
            t1 = FieldMul(t2, t1);
            t2 = FieldSquare(t1);
            for (int i = 1; i < 10; i++) t2 = FieldSquare(t2);
            t2 = FieldMul(t2, t1);
            long[] t3 = FieldSquare(t2);
            for (int i = 1; i < 20; i++) t3 = FieldSquare(t3);
            t2 = FieldMul(t3, t2);
            for (int i = 1; i < 11; i++) t2 = FieldSquare(t2);
            t1 = FieldMul(t2, t1);
            t2 = FieldSquare(t1);
            for (int i = 1; i < 50; i++) t2 = FieldSquare(t2);
            t2 = FieldMul(t2, t1);
            t3 = FieldSquare(t2);
            for (int i = 1; i < 100; i++) t3 = FieldSquare(t3);
            t2 = FieldMul(t3, t2);
            for (int i = 1; i < 51; i++) t2 = FieldSquare(t2);
            t1 = FieldMul(t2, t1);
            for (int i = 1; i < 6; i++) t1 = FieldSquare(t1);
            return FieldMul(t1, t0);
        }
    }
}

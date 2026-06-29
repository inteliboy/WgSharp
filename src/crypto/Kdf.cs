using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// WireGuard's KDF: HKDF using HMAC-BLAKE2s as the PRF, producing 1, 2, or 3
    /// 32-byte outputs. Used to advance the handshake chaining key and to derive
    /// transport keys. (RFC 5869 HKDF with BLAKE2s; WireGuard names it KDFn.)
    /// </summary>
    public static class Kdf
    {
        private const int BlockSize = 64; // BLAKE2s block size for HMAC padding
        private const int HashLen = 32;

        /// <summary>HMAC-BLAKE2s(key, data).</summary>
        public static byte[] Hmac(byte[] key, byte[] data)
        {
            byte[] k = key;
            if (k.Length > BlockSize) k = Blake2s.Hash(k, HashLen);
            byte[] kPad = new byte[BlockSize];
            Array.Copy(k, kPad, k.Length);

            byte[] inner = new byte[BlockSize];
            byte[] outer = new byte[BlockSize];
            for (int i = 0; i < BlockSize; i++)
            {
                inner[i] = (byte)(kPad[i] ^ 0x36);
                outer[i] = (byte)(kPad[i] ^ 0x5c);
            }

            var b1 = new Blake2s(HashLen);
            b1.Update(inner, 0, BlockSize);
            b1.Update(data, 0, data.Length);
            byte[] innerHash = b1.Finish();

            var b2 = new Blake2s(HashLen);
            b2.Update(outer, 0, BlockSize);
            b2.Update(innerHash, 0, innerHash.Length);
            return b2.Finish();
        }

        /// <summary>KDF1: one 32-byte output.</summary>
        public static byte[] Derive1(byte[] chainingKey, byte[] input)
        {
            byte[] prk = Hmac(chainingKey, input);          // extract
            byte[] t1 = Hmac(prk, One);                      // expand, T(1)
            return t1;
        }

        /// <summary>KDF2: two 32-byte outputs (e.g. new chaining key + key).</summary>
        public static void Derive2(byte[] chainingKey, byte[] input, out byte[] t1, out byte[] t2)
        {
            byte[] prk = Hmac(chainingKey, input);
            t1 = Hmac(prk, One);
            byte[] t1One = Concat(t1, Two);
            t2 = Hmac(prk, t1One);
        }

        /// <summary>KDF3: three 32-byte outputs (used when mixing in a PSK).</summary>
        public static void Derive3(byte[] chainingKey, byte[] input,
                                   out byte[] t1, out byte[] t2, out byte[] t3)
        {
            byte[] prk = Hmac(chainingKey, input);
            t1 = Hmac(prk, One);
            t2 = Hmac(prk, Concat(t1, Two));
            t3 = Hmac(prk, Concat(t2, Three));
        }

        private static readonly byte[] One = { 0x1 };
        private static readonly byte[] Two = { 0x2 };
        private static readonly byte[] Three = { 0x3 };

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}

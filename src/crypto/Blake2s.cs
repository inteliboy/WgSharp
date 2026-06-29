using System;

namespace WgSharp.Crypto
{
    /// <summary>
    /// BLAKE2s-256 (RFC 7693). Supports plain hashing and keyed mode (MAC).
    /// WireGuard uses this for the handshake hash chain, HKDF, and mac1/mac2.
    /// Variable digest length up to 32 bytes; WireGuard uses 32 (and 16 for mac1).
    /// </summary>
    public sealed class Blake2s
    {
        private static readonly uint[] IV =
        {
            0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
            0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u
        };

        private static readonly byte[,] Sigma =
        {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,15},
            {14,10, 4, 8, 9,15,13, 6, 1,12, 0, 2,11, 7, 5, 3},
            {11, 8,12, 0, 5, 2,15,13,10,14, 3, 6, 7, 1, 9, 4},
            { 7, 9, 3, 1,13,12,11,14, 2, 6, 5,10, 4, 0,15, 8},
            { 9, 0, 5, 7, 2, 4,10,15,14, 1,11,12, 6, 8, 3,13},
            { 2,12, 6,10, 0,11, 8, 3, 4,13, 7, 5,15,14, 1, 9},
            {12, 5, 1,15,14,13, 4,10, 0, 7, 6, 3, 9, 2, 8,11},
            {13,11, 7,14,12, 1, 3, 9, 5, 0,15, 4, 8, 6, 2,10},
            { 6,15,14, 9,11, 3, 0, 8,12, 2,13, 7, 1, 4,10, 5},
            {10, 2, 8, 4, 7, 6, 1, 5,15,11, 9,14, 3,12,13, 0}
        };

        private readonly uint[] _h = new uint[8];
        private readonly byte[] _buf = new byte[64];
        private int _bufLen;
        private ulong _counter;
        private readonly int _digestLen;

        public Blake2s(int digestLen = 32, byte[] key = null)
        {
            if (digestLen < 1 || digestLen > 32) throw new ArgumentException("digestLen 1..32");
            int keyLen = key == null ? 0 : key.Length;
            if (keyLen > 32) throw new ArgumentException("key <= 32 bytes");

            _digestLen = digestLen;
            Array.Copy(IV, _h, 8);
            // Parameter block: digest length, key length, fanout=1, depth=1.
            _h[0] ^= 0x01010000u ^ ((uint)keyLen << 8) ^ (uint)digestLen;

            if (keyLen > 0)
            {
                var block = new byte[64];
                Array.Copy(key, block, keyLen);
                Update(block, 0, 64); // keyed mode: first block is the padded key
            }
        }

        private static uint RotR(uint x, int n) { return (x >> n) | (x << (32 - n)); }

        private void Compress(byte[] block, int offset, bool last)
        {
            var m = new uint[16];
            for (int i = 0; i < 16; i++)
                m[i] = (uint)(block[offset + i * 4]
                            | block[offset + i * 4 + 1] << 8
                            | block[offset + i * 4 + 2] << 16
                            | block[offset + i * 4 + 3] << 24);

            var v = new uint[16];
            Array.Copy(_h, v, 8);
            Array.Copy(IV, 0, v, 8, 8);
            v[12] ^= (uint)(_counter & 0xFFFFFFFFu);
            v[13] ^= (uint)(_counter >> 32);
            if (last) v[14] = ~v[14];

            for (int r = 0; r < 10; r++)
            {
                G(v, m, 0, 4, 8, 12, Sigma[r, 0], Sigma[r, 1]);
                G(v, m, 1, 5, 9, 13, Sigma[r, 2], Sigma[r, 3]);
                G(v, m, 2, 6, 10, 14, Sigma[r, 4], Sigma[r, 5]);
                G(v, m, 3, 7, 11, 15, Sigma[r, 6], Sigma[r, 7]);
                G(v, m, 0, 5, 10, 15, Sigma[r, 8], Sigma[r, 9]);
                G(v, m, 1, 6, 11, 12, Sigma[r, 10], Sigma[r, 11]);
                G(v, m, 2, 7, 8, 13, Sigma[r, 12], Sigma[r, 13]);
                G(v, m, 3, 4, 9, 14, Sigma[r, 14], Sigma[r, 15]);
            }

            for (int i = 0; i < 8; i++) _h[i] ^= v[i] ^ v[i + 8];
        }

        private static void G(uint[] v, uint[] m, int a, int b, int c, int d, int x, int y)
        {
            v[a] = v[a] + v[b] + m[x]; v[d] = RotR(v[d] ^ v[a], 16);
            v[c] = v[c] + v[d];        v[b] = RotR(v[b] ^ v[c], 12);
            v[a] = v[a] + v[b] + m[y]; v[d] = RotR(v[d] ^ v[a], 8);
            v[c] = v[c] + v[d];        v[b] = RotR(v[b] ^ v[c], 7);
        }

        public void Update(byte[] data, int offset, int length)
        {
            while (length > 0)
            {
                if (_bufLen == 64)
                {
                    _counter += 64;
                    Compress(_buf, 0, false);
                    _bufLen = 0;
                }
                int take = Math.Min(64 - _bufLen, length);
                Array.Copy(data, offset, _buf, _bufLen, take);
                _bufLen += take;
                offset += take;
                length -= take;
            }
        }

        public byte[] Finish()
        {
            _counter += (ulong)_bufLen;
            for (int i = _bufLen; i < 64; i++) _buf[i] = 0;
            Compress(_buf, 0, true);

            var outp = new byte[_digestLen];
            for (int i = 0; i < _digestLen; i++)
                outp[i] = (byte)(_h[i >> 2] >> (8 * (i & 3)));
            return outp;
        }

        // --- convenience one-shots ---
        public static byte[] Hash(byte[] data, int digestLen = 32)
        {
            var b = new Blake2s(digestLen);
            b.Update(data, 0, data.Length);
            return b.Finish();
        }

        public static byte[] HashKeyed(byte[] key, byte[] data, int digestLen = 32)
        {
            var b = new Blake2s(digestLen, key);
            b.Update(data, 0, data.Length);
            return b.Finish();
        }
    }
}

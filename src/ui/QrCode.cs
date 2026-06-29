using System;
using System.Collections.Generic;

namespace WgSharp.Ui
{
    /// <summary>
    /// A self-contained QR Code encoder (byte mode) sufficient to encode a
    /// WireGuard .conf so the official mobile app can scan it. Implements the
    /// QR spec: data encoding, Reed-Solomon ECC, block interleaving, matrix
    /// construction, masking, and format/version info. Supports versions 1-40.
    ///
    /// Adapted to a minimal, dependency-free form. Returns a boolean module grid
    /// (true = dark) for rendering.
    /// </summary>
    public static partial class QrCode
    {
        public enum Ecc { L = 0, M = 1, Q = 2, H = 3 }

        public static bool[,] Encode(string text, Ecc ecc)
        {
            byte[] data = System.Text.Encoding.UTF8.GetBytes(text);

            int version = ChooseVersion(data.Length, ecc);
            if (version < 0) throw new Exception("Data too large for a QR code.");

            // --- build the bit stream: mode(4) + length + data ---
            var bits = new BitBuffer();
            bits.AppendBits(0x4, 4); // byte mode
            int lenBits = (version <= 9) ? 8 : 16;
            bits.AppendBits(data.Length, lenBits);
            foreach (byte b in data) bits.AppendBits(b, 8);

            int totalDataCodewords = DataCodewords(version, ecc);
            int capacityBits = totalDataCodewords * 8;
            // terminator
            int term = System.Math.Min(4, capacityBits - bits.Length);
            bits.AppendBits(0, term);
            // pad to byte boundary
            while (bits.Length % 8 != 0) bits.AppendBits(0, 1);
            // pad bytes
            byte[] pad = { 0xEC, 0x11 };
            int pi = 0;
            while (bits.Length < capacityBits) { bits.AppendBits(pad[pi & 1], 8); pi++; }

            byte[] dataCodewords = bits.ToBytes();

            // --- ECC + interleave ---
            byte[] finalCodewords = AddEccAndInterleave(dataCodewords, version, ecc);

            // --- draw matrix ---
            int size = version * 4 + 17;
            var modules = new bool[size, size];
            var isFunction = new bool[size, size];
            DrawFunctionPatterns(modules, isFunction, version);
            DrawCodewords(modules, isFunction, finalCodewords);

            // pick a mask (try all 8, choose lowest penalty)
            int bestMask = 0; long bestPenalty = long.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                ApplyMask(modules, isFunction, m);
                DrawFormatBits(modules, isFunction, ecc, m);
                long p = Penalty(modules);
                if (p < bestPenalty) { bestPenalty = p; bestMask = m; }
                ApplyMask(modules, isFunction, m); // undo (XOR again)
            }
            ApplyMask(modules, isFunction, bestMask);
            DrawFormatBits(modules, isFunction, ecc, bestMask);

            return modules;
        }

        // ---------------- capacity tables ----------------
        // Data codeword counts per (version, ecc). Indexed [version-1][ecc].
        // From the QR spec (ISO/IEC 18004). Compact subset of the full table.
        private static readonly int[][] DataCodewordsTable = BuildDataCodewordsTable();

        private static int DataCodewords(int version, Ecc ecc)
        {
            return DataCodewordsTable[version - 1][(int)ecc];
        }

        private static int ChooseVersion(int dataLen, Ecc ecc)
        {
            for (int v = 1; v <= 40; v++)
            {
                int cap = DataCodewordsTable[v - 1][(int)ecc];
                int lenBits = (v <= 9) ? 8 : 16;
                // bits needed: mode(4) + lenBits + data*8, must fit cap*8
                int need = 4 + lenBits + dataLen * 8;
                if (need <= cap * 8) return v;
            }
            return -1;
        }

        // ECC codewords per block and block counts: [version-1][ecc] -> (eccPerBlock, numBlocksG1, dataPerBlockG1, numBlocksG2, dataPerBlockG2)
        private static readonly int[][][] EccTable = BuildEccTable();

        // ---------------- Reed-Solomon ----------------
        private static byte[] AddEccAndInterleave(byte[] data, int version, Ecc ecc)
        {
            int[] e = EccTable[version - 1][(int)ecc];
            int eccLen = e[0], numG1 = e[1], dataG1 = e[2], numG2 = e[3], dataG2 = e[4];
            int numBlocks = numG1 + numG2;

            var blocks = new List<byte[]>();
            var eccBlocks = new List<byte[]>();
            byte[] gen = RsGenerator(eccLen);
            int pos = 0;
            for (int b = 0; b < numBlocks; b++)
            {
                int dlen = (b < numG1) ? dataG1 : dataG2;
                var blk = new byte[dlen];
                Array.Copy(data, pos, blk, 0, dlen);
                pos += dlen;
                blocks.Add(blk);
                eccBlocks.Add(RsRemainder(blk, gen, eccLen));
            }

            var result = new List<byte>();
            int maxData = System.Math.Max(dataG1, dataG2);
            for (int i = 0; i < maxData; i++)
                for (int b = 0; b < numBlocks; b++)
                    if (i < blocks[b].Length) result.Add(blocks[b][i]);
            for (int i = 0; i < eccLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    result.Add(eccBlocks[b][i]);
            return result.ToArray();
        }

        private static byte[] RsGenerator(int degree)
        {
            // Generator polynomial = product of (x - alpha^i) for i in [0, degree).
            byte[] g = { 1 };
            for (int i = 0; i < degree; i++)
                g = PolyMul(g, new byte[] { 1, GfExp(i) });
            return g;
        }

        private static byte[] PolyMul(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length - 1];
            for (int i = 0; i < a.Length; i++)
                for (int j = 0; j < b.Length; j++)
                    r[i + j] ^= GfMul(a[i], b[j]);
            return r;
        }

        private static byte[] RsRemainder(byte[] data, byte[] gen, int eccLen)
        {
            // gen has eccLen+1 coefficients (monic). Use gen[1..] against the
            // eccLen-length remainder buffer.
            byte[] result = new byte[eccLen];
            foreach (byte db in data)
            {
                byte factor = (byte)(db ^ result[0]);
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < eccLen; i++)
                    result[i] ^= GfMul(gen[i + 1], factor);
            }
            return result;
        }

        // ---------------- GF(256) arithmetic ----------------
        private static readonly byte[] GfExpTable = new byte[512];
        private static readonly byte[] GfLogTable = new byte[256];
        static QrCode()
        {
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                GfExpTable[i] = (byte)x;
                GfLogTable[x] = (byte)i;
                x <<= 1;
                if ((x & 0x100) != 0) x ^= 0x11D; // primitive polynomial
            }
            for (int i = 255; i < 512; i++) GfExpTable[i] = GfExpTable[i - 255];
        }
        private static byte GfExp(int e) { return GfExpTable[((e % 255) + 255) % 255]; }
        private static byte GfMul(byte a, byte b)
        {
            if (a == 0 || b == 0) return 0;
            return GfExpTable[GfLogTable[a] + GfLogTable[b]];
        }

        // ---------------- matrix construction ----------------
        private static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version)
        {
            int size = m.GetLength(0);
            // timing patterns
            for (int i = 0; i < size; i++)
            {
                SetFunc(m, f, 6, i, i % 2 == 0);
                SetFunc(m, f, i, 6, i % 2 == 0);
            }
            // finder patterns + separators
            DrawFinder(m, f, 3, 3);
            DrawFinder(m, f, size - 4, 3);
            DrawFinder(m, f, 3, size - 4);

            // alignment patterns. Only the three that coincide with the finder
            // corners are skipped; patterns whose center lies on the timing line
            // are still drawn (they overlap the timing pattern, which is fine).
            int[] pos = AlignmentPositions(version);
            for (int i = 0; i < pos.Length; i++)
                for (int j = 0; j < pos.Length; j++)
                {
                    int r = pos[i], c = pos[j];
                    if ((r <= 8 && c <= 8) || (r <= 8 && c >= size - 9) || (r >= size - 9 && c <= 8)) continue;
                    DrawAlignment(m, f, r, c);
                }

            // reserve format info area
            for (int i = 0; i < 9; i++) { if (!f[i, 8]) Reserve(f, i, 8); if (!f[8, i]) Reserve(f, 8, i); }
            for (int i = 0; i < 8; i++) { Reserve(f, size - 1 - i, 8); Reserve(f, 8, size - 1 - i); }
            SetFunc(m, f, size - 8, 8, true); // dark module

            // version info (v >= 7)
            if (version >= 7)
            {
                long vinfo = VersionInfo(version);
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((vinfo >> i) & 1) != 0;
                    int a = i / 3, b = i % 3;
                    SetFunc(m, f, size - 11 + b, a, bit);
                    SetFunc(m, f, a, size - 11 + b, bit);
                }
            }
        }

        private static void DrawFinder(bool[,] m, bool[,] f, int cx, int cy)
        {
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= m.GetLength(0) || y >= m.GetLength(0)) continue;
                    int dist = System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy));
                    SetFunc(m, f, y, x, dist != 2 && dist != 4);
                }
        }

        private static void DrawAlignment(bool[,] m, bool[,] f, int cy, int cx)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int dist = System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy));
                    SetFunc(m, f, cy + dy, cx + dx, dist != 1);
                }
        }

        private static void SetFunc(bool[,] m, bool[,] f, int r, int c, bool val)
        {
            if (r < 0 || c < 0 || r >= m.GetLength(0) || c >= m.GetLength(0)) return;
            m[r, c] = val; f[r, c] = true;
        }
        private static void Reserve(bool[,] f, int r, int c)
        {
            if (r < 0 || c < 0 || r >= f.GetLength(0) || c >= f.GetLength(0)) return;
            f[r, c] = true;
        }

        private static void DrawCodewords(bool[,] m, bool[,] f, byte[] codewords)
        {
            int size = m.GetLength(0);
            int bitIndex = 0;
            int total = codewords.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int c = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int r = upward ? size - 1 - vert : vert;
                        if (f[r, c]) continue;
                        bool dark = false;
                        if (bitIndex < total)
                        {
                            int bytePos = bitIndex >> 3;
                            int bit = 7 - (bitIndex & 7);
                            dark = ((codewords[bytePos] >> bit) & 1) != 0;
                            bitIndex++;
                        }
                        m[r, c] = dark;
                    }
                }
            }
        }

        private static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    if (f[r, c]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (r + c) % 2 == 0; break;
                        case 1: invert = r % 2 == 0; break;
                        case 2: invert = c % 3 == 0; break;
                        case 3: invert = (r + c) % 3 == 0; break;
                        case 4: invert = (r / 2 + c / 3) % 2 == 0; break;
                        case 5: invert = (r * c) % 2 + (r * c) % 3 == 0; break;
                        case 6: invert = ((r * c) % 2 + (r * c) % 3) % 2 == 0; break;
                        default: invert = ((r + c) % 2 + (r * c) % 3) % 2 == 0; break;
                    }
                    if (invert) m[r, c] = !m[r, c];
                }
        }

        private static void DrawFormatBits(bool[,] m, bool[,] f, Ecc ecc, int mask)
        {
            int size = m.GetLength(0);
            int data = ((EccFormatBits((int)ecc)) << 3) | mask;
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            // Format bits are placed MSB-first: placement position i reads bit
            // (14 - i) of the 15-bit value. (Verified by decoding with a QR reader.)
            for (int i = 0; i <= 5; i++) SetFmt(m, 8, i, GetBit(bits, 14 - i));
            SetFmt(m, 8, 7, GetBit(bits, 14 - 6));
            SetFmt(m, 8, 8, GetBit(bits, 14 - 7));
            SetFmt(m, 7, 8, GetBit(bits, 14 - 8));
            for (int i = 9; i < 15; i++) SetFmt(m, 14 - i, 8, GetBit(bits, 14 - i));

            for (int i = 0; i < 8; i++) SetFmt(m, size - 1 - i, 8, GetBit(bits, 14 - i));
            for (int i = 8; i < 15; i++) SetFmt(m, 8, size - 15 + i, GetBit(bits, 14 - i));
            m[size - 8, 8] = true; // dark module
        }

        private static void SetFmt(bool[,] m, int r, int c, bool v) { m[r, c] = v; }
        private static bool GetBit(int x, int i) { return ((x >> i) & 1) != 0; }
        private static int EccFormatBits(int ecc)
        {
            // mapping: L=01, M=00, Q=11, H=10
            switch (ecc) { case 0: return 1; case 1: return 0; case 2: return 3; default: return 2; }
        }

        private static long Penalty(bool[,] m)
        {
            int size = m.GetLength(0);
            long penalty = 0;

            // Rule 1: runs of 5+ same-color modules in rows and columns.
            for (int r = 0; r < size; r++)
            {
                int run = 1;
                for (int c = 1; c < size; c++)
                {
                    if (m[r, c] == m[r, c - 1]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                    else run = 1;
                }
            }
            for (int c = 0; c < size; c++)
            {
                int run = 1;
                for (int r = 1; r < size; r++)
                {
                    if (m[r, c] == m[r - 1, c]) { run++; if (run == 5) penalty += 3; else if (run > 5) penalty++; }
                    else run = 1;
                }
            }

            // Rule 2: 2x2 blocks of the same color (+3 each).
            for (int r = 0; r < size - 1; r++)
                for (int c = 0; c < size - 1; c++)
                {
                    bool v = m[r, c];
                    if (v == m[r, c + 1] && v == m[r + 1, c] && v == m[r + 1, c + 1])
                        penalty += 3;
                }

            // Rule 3: finder-like pattern 1:1:3:1:1 with 4 light modules on either
            // side (+40 each), checked horizontally and vertically.
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                {
                    if (c + 6 < size && MatchFinderRun(m, r, c, true)) penalty += 40;
                    if (r + 6 < size && MatchFinderRun(m, r, c, false)) penalty += 40;
                }

            // Rule 4: proportion of dark modules deviating from 50% (+10 per 5%).
            int dark = 0;
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (m[r, c]) dark++;
            int total = size * size;
            int percent = (dark * 100) / total;
            int dev = System.Math.Abs(percent - 50) / 5;
            penalty += dev * 10;

            return penalty;
        }

        // The 1:1:3:1:1 dark/light pattern with 4-module light margin, per rule 3.
        private static bool MatchFinderRun(bool[,] m, int r, int c, bool horizontal)
        {
            bool[] pat = { true, false, true, true, true, false, true };
            for (int k = 0; k < 7; k++)
            {
                bool cell = horizontal ? m[r, c + k] : m[r + k, c];
                if (cell != pat[k]) return false;
            }
            // require 4 light modules on one side
            bool lightBefore = true, lightAfter = true;
            for (int k = 1; k <= 4; k++)
            {
                if (horizontal)
                {
                    if (c - k >= 0 && m[r, c - k]) lightBefore = false;
                    if (c + 6 + k < m.GetLength(0) && m[r, c + 6 + k]) lightAfter = false;
                }
                else
                {
                    if (r - k >= 0 && m[r - k, c]) lightBefore = false;
                    if (r + 6 + k < m.GetLength(0) && m[r + 6 + k, c]) lightAfter = false;
                }
            }
            return lightBefore || lightAfter;
        }

        private static int[] AlignmentPositions(int version)
        {
            if (version == 1) return new int[0];
            int num = version / 7 + 2;
            int step;
            if (version == 32) step = 26;
            else step = (version * 4 + 4) / (num * 2 - 2) * 2;
            int[] result = new int[num];
            int size = version * 4 + 17;
            result[0] = 6;
            for (int i = num - 1, p = size - 7; i >= 1; i--, p -= step) result[i] = p;
            return result;
        }

        private static long VersionInfo(int version)
        {
            int rem = version;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            return ((long)version << 12) | (uint)rem;
        }

        // ---------------- big static tables ----------------
        private static int[][] BuildDataCodewordsTable()
        {
            // total data codewords per version/ecc (L,M,Q,H)
            int[][] t = new int[40][];
            int[,] raw = {
                {19,16,13,9},{34,28,22,16},{55,44,34,26},{80,64,48,36},{108,86,62,46},
                {136,108,76,60},{156,124,88,66},{194,154,110,86},{232,182,132,100},{274,216,154,122},
                {324,254,180,140},{370,290,206,158},{428,334,244,180},{461,365,261,197},{523,415,295,223},
                {589,453,325,253},{647,507,367,283},{721,563,397,313},{795,627,445,341},{861,669,485,385},
                {932,714,512,406},{1006,782,568,442},{1094,860,614,464},{1174,914,664,514},{1276,1000,718,538},
                {1370,1062,754,596},{1468,1128,808,628},{1531,1193,871,661},{1631,1267,911,701},{1735,1373,985,745},
                {1843,1455,1033,793},{1955,1541,1115,845},{2071,1631,1171,901},{2191,1725,1231,961},{2306,1812,1286,986},
                {2434,1914,1354,1054},{2566,1992,1426,1096},{2702,2102,1502,1142},{2812,2216,1582,1222},{2956,2334,1666,1276}
            };
            for (int v = 0; v < 40; v++) t[v] = new int[] { raw[v, 0], raw[v, 1], raw[v, 2], raw[v, 3] };
            return t;
        }

        private static int[][][] BuildEccTable()
        {
            // [version][ecc] = {eccPerBlock, numG1, dataG1, numG2, dataG2}
            // Compact table for versions 1-40, all four ECC levels.
            // Source: ISO/IEC 18004 Annex.
            int[][][] t = new int[40][][];
            int[][,] all = EccRaw();
            for (int v = 0; v < 40; v++)
            {
                t[v] = new int[4][];
                for (int e = 0; e < 4; e++)
                    t[v][e] = new int[] { all[v][e, 0], all[v][e, 1], all[v][e, 2], all[v][e, 3], all[v][e, 4] };
            }
            return t;
        }

        // Raw ECC parameters. Each version: 4 rows (L,M,Q,H), each row:
        // {eccPerBlock, numBlocksGroup1, dataCwGroup1, numBlocksGroup2, dataCwGroup2}
        private static int[][,] EccRaw()
        {
            return new int[][,] {
                // v1
                new int[,]{{7,1,19,0,0},{10,1,16,0,0},{13,1,13,0,0},{17,1,9,0,0}},
                new int[,]{{10,1,34,0,0},{16,1,28,0,0},{22,1,22,0,0},{28,1,16,0,0}},
                new int[,]{{15,1,55,0,0},{26,1,44,0,0},{18,2,17,0,0},{22,2,13,0,0}},
                new int[,]{{20,1,80,0,0},{18,2,32,0,0},{26,2,24,0,0},{16,4,9,0,0}},
                new int[,]{{26,1,108,0,0},{24,2,43,0,0},{18,2,15,2,16},{22,2,11,2,12}},
                new int[,]{{18,2,68,0,0},{16,4,27,0,0},{24,4,19,0,0},{28,4,15,0,0}},
                new int[,]{{20,2,78,0,0},{18,4,31,0,0},{18,2,14,4,15},{26,4,13,1,14}},
                new int[,]{{24,2,97,0,0},{22,2,38,2,39},{22,4,18,2,19},{26,4,14,2,15}},
                new int[,]{{30,2,116,0,0},{22,3,36,2,37},{20,4,16,4,17},{24,4,12,4,13}},
                new int[,]{{18,2,68,2,69},{26,4,43,1,44},{24,6,19,2,20},{28,6,15,2,16}},
                new int[,]{{20,4,81,0,0},{30,1,50,4,51},{28,4,22,4,23},{24,3,12,8,13}},
                new int[,]{{24,2,92,2,93},{22,6,36,2,37},{26,4,20,6,21},{28,7,14,4,15}},
                new int[,]{{26,4,107,0,0},{22,8,37,1,38},{24,8,20,4,21},{22,12,11,4,12}},
                new int[,]{{30,3,115,1,116},{24,4,40,5,41},{20,11,16,5,17},{24,11,12,5,13}},
                new int[,]{{22,5,87,1,88},{24,5,41,5,42},{30,5,24,7,25},{24,11,12,7,13}},
                new int[,]{{24,5,98,1,99},{28,7,45,3,46},{24,15,19,2,20},{30,3,15,13,16}},
                new int[,]{{28,1,107,5,108},{28,10,46,1,47},{28,1,22,15,23},{28,2,14,17,15}},
                new int[,]{{30,5,120,1,121},{26,9,43,4,44},{28,17,22,1,23},{28,2,14,19,15}},
                new int[,]{{28,3,113,4,114},{26,3,44,11,45},{26,17,21,4,22},{26,9,13,16,14}},
                new int[,]{{28,3,107,5,108},{26,3,41,13,42},{30,15,24,5,25},{28,15,15,10,16}},
                new int[,]{{28,4,116,4,117},{26,17,42,0,0},{28,17,22,6,23},{30,19,16,6,17}},
                new int[,]{{28,2,111,7,112},{28,17,46,0,0},{30,7,24,16,25},{24,34,13,0,0}},
                new int[,]{{30,4,121,5,122},{28,4,47,14,48},{30,11,24,14,25},{30,16,15,14,16}},
                new int[,]{{30,6,117,4,118},{28,6,45,14,46},{30,11,24,16,25},{30,30,16,2,17}},
                new int[,]{{26,8,106,4,107},{28,8,47,13,48},{30,7,24,22,25},{30,22,15,13,16}},
                new int[,]{{28,10,114,2,115},{28,19,46,4,47},{28,28,22,6,23},{30,33,16,4,17}},
                new int[,]{{30,8,122,4,123},{28,22,45,3,46},{30,8,23,26,24},{30,12,15,28,16}},
                new int[,]{{30,3,117,10,118},{28,3,45,23,46},{30,4,24,31,25},{30,11,15,31,16}},
                new int[,]{{30,7,116,7,117},{28,21,45,7,46},{30,1,23,37,24},{30,19,15,26,16}},
                new int[,]{{30,5,115,10,116},{28,19,47,10,48},{30,15,24,25,25},{30,23,15,25,16}},
                new int[,]{{30,13,115,3,116},{28,2,46,29,47},{30,42,24,1,25},{30,23,15,28,16}},
                new int[,]{{30,17,115,0,0},{28,10,46,23,47},{30,10,24,35,25},{30,19,15,35,16}},
                new int[,]{{30,17,115,1,116},{28,14,46,21,47},{30,29,24,19,25},{30,11,15,46,16}},
                new int[,]{{30,13,115,6,116},{28,14,46,23,47},{30,44,24,7,25},{30,59,16,1,17}},
                new int[,]{{30,12,121,7,122},{28,12,47,26,48},{30,39,24,14,25},{30,22,15,41,16}},
                new int[,]{{30,6,121,14,122},{28,6,47,34,48},{30,46,24,10,25},{30,2,15,64,16}},
                new int[,]{{30,17,122,4,123},{28,29,46,14,47},{30,49,24,10,25},{30,24,15,46,16}},
                new int[,]{{30,4,122,18,123},{28,13,46,32,47},{30,48,24,14,25},{30,42,15,32,16}},
                new int[,]{{30,20,117,4,118},{28,40,47,7,48},{30,43,24,22,25},{30,10,15,67,16}},
                new int[,]{{30,19,118,6,119},{28,18,47,31,48},{30,34,24,34,25},{30,20,15,61,16}}
            };
        }

        // ---------------- bit buffer ----------------
        private sealed class BitBuffer
        {
            private readonly List<bool> _bits = new List<bool>();
            public int Length { get { return _bits.Count; } }
            public void AppendBits(int value, int len)
            {
                for (int i = len - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
            }
            public byte[] ToBytes()
            {
                byte[] b = new byte[(_bits.Count + 7) / 8];
                for (int i = 0; i < _bits.Count; i++)
                    if (_bits[i]) b[i >> 3] |= (byte)(1 << (7 - (i & 7)));
                return b;
            }
        }
    }
}

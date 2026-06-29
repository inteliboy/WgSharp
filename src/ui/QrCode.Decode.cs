using System;
using System.Collections.Generic;

namespace WgSharp.Ui
{
    /// <summary>
    /// QR decoding, sharing the encoder's GF(256) arithmetic, ECC tables, and
    /// module-layout logic (function patterns, masking, zigzag codeword order)
    /// from QrCode.cs via this partial class — same matrix conventions in both
    /// directions, so there's no separate "decoder's idea of the layout" to
    /// drift out of sync with the encoder.
    ///
    /// Entry point: <see cref="Decode"/> takes a module grid (true = dark, the
    /// same representation QrImageLocator produces from a camera frame) and
    /// returns the original text, or null if it can't be decoded (wrong
    /// format-info read, uncorrectable errors, or an unsupported encoding).
    ///
    /// Scope: byte mode (what WgSharp's own encoder and most general-purpose
    /// QR generators use for arbitrary text/config payloads), plus numeric and
    /// alphanumeric mode for completeness, and skips over an ECI designator
    /// if present (common in general-purpose QR generators that flag UTF-8
    /// even for plain ASCII payloads) rather than aborting on it. Kanji mode
    /// is not handled.
    /// </summary>
    public static partial class QrCode
    {
        /// <summary>Decodes a module grid back to text, or null on failure.</summary>
        public static string Decode(bool[,] modules)
        {
            try
            {
                int size = modules.GetLength(0);
                if (size < 21 || (size - 17) % 4 != 0) return null;
                int version = (size - 17) / 4;
                if (version < 1 || version > 40) return null;

                // We only need which cells are function modules, not their
                // (synthetic) colors, so DrawFunctionPatterns writes into a
                // throwaway grid — the real, camera-read colors in `modules`
                // are left untouched.
                bool[,] throwaway = new bool[size, size];
                bool[,] isFunction = new bool[size, size];
                DrawFunctionPatterns(throwaway, isFunction, version);

                Ecc ecc; int mask;
                if (!ReadFormatInfo(modules, size, out ecc, out mask)) return null;

                bool[,] data = (bool[,])modules.Clone();
                ApplyMask(data, isFunction, mask); // XOR is its own inverse: this removes the mask

                byte[] codewords = ExtractCodewords(data, isFunction, version, ecc);
                byte[] dataBytes = DeinterleaveAndCorrect(codewords, version, ecc);
                if (dataBytes == null) return null;

                return DecodeBitStream(dataBytes, version);
            }
            catch
            {
                // Any unexpected shape mismatch (corrupted/partial grid from a
                // bad camera read) is a decode failure, not a crash.
                return null;
            }
        }

        // ---------------- format info (ecc level + mask) ----------------
        // Rather than implement a separate BCH(15,5) error-correcting decode
        // for the two redundant format-info copies, brute force is simpler and
        // just as robust here: only 32 (ecc, mask) combinations exist. Compute
        // each one's expected 15-bit pattern with the exact same formula
        // DrawFormatBits uses to WRITE it, compare against both copies read
        // from the image, and keep the combination with the lowest total
        // Hamming distance across all 30 read bits.
        private static bool ReadFormatInfo(bool[,] m, int size, out Ecc ecc, out int mask)
        {
            int bestDist = int.MaxValue, bestEcc = 0, bestMask = 0;
            for (int e = 0; e < 4; e++)
            {
                for (int mk = 0; mk < 8; mk++)
                {
                    int fdata = (EccFormatBits(e) << 3) | mk;
                    int rem = fdata;
                    for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
                    int bits = ((fdata << 10) | rem) ^ 0x5412;

                    int dist = 0;
                    // copy 1: around the top-left finder pattern.
                    for (int i = 0; i <= 5; i++) dist += m[8, i] != GetBit(bits, 14 - i) ? 1 : 0;
                    dist += m[8, 7] != GetBit(bits, 14 - 6) ? 1 : 0;
                    dist += m[8, 8] != GetBit(bits, 14 - 7) ? 1 : 0;
                    dist += m[7, 8] != GetBit(bits, 14 - 8) ? 1 : 0;
                    for (int i = 9; i < 15; i++) dist += m[14 - i, 8] != GetBit(bits, 14 - i) ? 1 : 0;
                    // copy 2: split across the top-right and bottom-left finders.
                    for (int i = 0; i < 8; i++) dist += m[size - 1 - i, 8] != GetBit(bits, 14 - i) ? 1 : 0;
                    for (int i = 8; i < 15; i++) dist += m[8, size - 15 + i] != GetBit(bits, 14 - i) ? 1 : 0;

                    if (dist < bestDist) { bestDist = dist; bestEcc = e; bestMask = mk; }
                }
            }
            ecc = (Ecc)bestEcc;
            mask = bestMask;
            // Out of 30 read bits, tolerate a modest number of misreads (camera
            // noise/sampling jitter) before giving up — an arbitrary but
            // generous-enough threshold given the search already picks the
            // closest of only 32 candidates.
            return bestDist <= 8;
        }

        // ---------------- codeword extraction ----------------
        // Mirrors DrawCodewords's zigzag traversal exactly, but reads bits
        // instead of writing them.
        private static byte[] ExtractCodewords(bool[,] m, bool[,] f, int version, Ecc ecc)
        {
            int size = m.GetLength(0);
            int[] e = EccTable[version - 1][(int)ecc];
            int eccLen = e[0], numG1 = e[1], dataG1 = e[2], numG2 = e[3], dataG2 = e[4];
            int numBlocks = numG1 + numG2;
            int totalDataCw = numG1 * dataG1 + numG2 * dataG2;
            int totalCw = totalDataCw + eccLen * numBlocks;

            byte[] codewords = new byte[totalCw];
            int bitIndex = 0;
            int totalBits = totalCw * 8;
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
                        if (bitIndex < totalBits)
                        {
                            int bytePos = bitIndex >> 3;
                            int bit = 7 - (bitIndex & 7);
                            if (m[r, c]) codewords[bytePos] |= (byte)(1 << bit);
                            bitIndex++;
                        }
                    }
                }
            }
            return codewords;
        }

        // ---------------- de-interleave + Reed-Solomon correction ----------------
        // Reverses AddEccAndInterleave's block layout, then RS-corrects each
        // block independently using its own data+ecc codewords.
        private static byte[] DeinterleaveAndCorrect(byte[] codewords, int version, Ecc ecc)
        {
            int[] e = EccTable[version - 1][(int)ecc];
            int eccLen = e[0], numG1 = e[1], dataG1 = e[2], numG2 = e[3], dataG2 = e[4];
            int numBlocks = numG1 + numG2;
            int maxData = Math.Max(dataG1, dataG2);

            int[] blockLen = new int[numBlocks];
            byte[][] blocks = new byte[numBlocks][];
            for (int b = 0; b < numBlocks; b++)
            {
                blockLen[b] = (b < numG1) ? dataG1 : dataG2;
                blocks[b] = new byte[blockLen[b] + eccLen];
            }

            int idx = 0;
            for (int i = 0; i < maxData; i++)
                for (int b = 0; b < numBlocks; b++)
                    if (i < blockLen[b]) blocks[b][i] = codewords[idx++];
            for (int i = 0; i < eccLen; i++)
                for (int b = 0; b < numBlocks; b++)
                    blocks[b][blockLen[b] + i] = codewords[idx++];

            byte[] result = new byte[numG1 * dataG1 + numG2 * dataG2];
            int outPos = 0;
            for (int b = 0; b < numBlocks; b++)
            {
                byte[] corrected = RsCorrect(blocks[b], blockLen[b], eccLen);
                if (corrected == null) return null; // uncorrectable block
                Array.Copy(corrected, 0, result, outPos, blockLen[b]);
                outPos += blockLen[b];
            }
            return result;
        }

        /// <summary>
        /// Reed-Solomon error correction for one block (data+ecc codewords,
        /// high-degree-first — block[0] is the first/highest-degree codeword,
        /// matching RsRemainder's convention in the encoder). Returns the
        /// corrected DATA portion (length dataLen), or null if the block has
        /// more errors than eccLen/2 can fix.
        /// </summary>
        private static byte[] RsCorrect(byte[] block, int dataLen, int eccLen)
        {
            int n = block.Length;
            byte[] syndromes = new byte[eccLen];
            bool allZero = true;
            for (int j = 0; j < eccLen; j++)
            {
                syndromes[j] = EvalPolyHighFirst(block, GfExp(j));
                if (syndromes[j] != 0) allZero = false;
            }
            if (allZero)
            {
                byte[] clean = new byte[dataLen];
                Array.Copy(block, 0, clean, 0, dataLen);
                return clean;
            }

            byte[] sigma; int errCount;
            if (!BerlekampMassey(syndromes, eccLen, out sigma, out errCount)) return null;
            if (errCount == 0) return null; // syndromes nonzero but BM found no errors -> inconsistent, bail

            int[] positions, exps;
            if (!ChienSearch(sigma, errCount, n, out positions, out exps)) return null;

            // Formal derivative of sigma(x) over a characteristic-2 field: only
            // odd-degree terms survive, shifted down one degree, coefficient
            // unchanged (even-degree terms are added to themselves an even
            // number of times under "multiply coefficient by its degree", i.e.
            // XORed away).
            byte[] deriv = new byte[errCount];
            for (int j = 0; j < errCount; j++) deriv[j] = (j % 2 == 0) ? sigma[j + 1] : (byte)0;

            // Error evaluator Omega(x) = [S(x)*sigma(x)] mod x^eccLen. PolyMul
            // is the encoder's existing convolution helper — convolution itself
            // doesn't care which end is "high" or "low"; here both syndromes
            // and sigma are low-degree-first (syndromes[j] is genuinely the
            // coefficient of x^j by definition of the auxiliary polynomial).
            byte[] omegaFull = PolyMul(syndromes, sigma);
            byte[] omega = new byte[eccLen];
            for (int i = 0; i < eccLen && i < omegaFull.Length; i++) omega[i] = omegaFull[i];

            byte[] corrected = (byte[])block.Clone();
            for (int i = 0; i < positions.Length; i++)
            {
                byte xinv = GfExp(exps[i]);
                byte omegaVal = EvalLowHorner(omega, xinv);
                byte derivVal = EvalLowHorner(deriv, xinv);
                if (derivVal == 0) return null; // shouldn't happen; guards a divide-by-zero
                byte mag = GfMul(omegaVal, GfInverse(derivVal));
                corrected[positions[i]] ^= mag;
            }

            // Verify: recompute syndromes of the corrected block. If they're
            // not all zero, treat the whole block as uncorrectable rather than
            // hand back data that LOOKS corrected but isn't (e.g. more errors
            // were present than eccLen/2 could actually guarantee fixing).
            for (int j = 0; j < eccLen; j++)
                if (EvalPolyHighFirst(corrected, GfExp(j)) != 0) return null;

            byte[] data = new byte[dataLen];
            Array.Copy(corrected, 0, data, 0, dataLen);
            return data;
        }

        /// <summary>
        /// Berlekamp-Massey: finds the error locator polynomial sigma(x) from
        /// N syndromes. Returns sigma low-degree-first (sigma[0] is always 1),
        /// length errCount+1, and errCount = the polynomial's degree (number
        /// of errors). Returns false if more errors are implied than this
        /// block's ECC strength (N/2) can guarantee correcting.
        /// </summary>
        private static bool BerlekampMassey(byte[] S, int N, out byte[] sigma, out int errCount)
        {
            byte[] C = new byte[N + 1]; C[0] = 1;
            byte[] B = new byte[N + 1]; B[0] = 1;
            int L = 0; byte b = 1; int m = 1;

            for (int n = 0; n < N; n++)
            {
                byte delta = S[n];
                for (int i = 1; i <= L; i++) delta ^= GfMul(C[i], S[n - i]);

                if (delta == 0)
                {
                    m++;
                }
                else if (2 * L <= n)
                {
                    byte[] t = (byte[])C.Clone();
                    byte coef = GfMul(delta, GfInverse(b));
                    for (int i = 0; i < B.Length && i + m < C.Length; i++)
                        C[i + m] ^= GfMul(coef, B[i]);
                    L = n + 1 - L;
                    B = t;
                    b = delta;
                    m = 1;
                }
                else
                {
                    byte coef = GfMul(delta, GfInverse(b));
                    for (int i = 0; i < B.Length && i + m < C.Length; i++)
                        C[i + m] ^= GfMul(coef, B[i]);
                    m++;
                }
            }

            if (2 * L > N) { sigma = null; errCount = 0; return false; }
            sigma = new byte[L + 1];
            Array.Copy(C, sigma, L + 1);
            errCount = L;
            return true;
        }

        /// <summary>
        /// Chien search: finds the roots of sigma(x), giving the error
        /// positions (0-indexed from the start of the block, matching the same
        /// indexing as the block/codeword array) and the root exponents
        /// (needed by Forney's algorithm for the magnitudes). Fails if it
        /// can't find exactly errCount roots (degree mismatch = uncorrectable).
        /// </summary>
        private static bool ChienSearch(byte[] sigma, int errCount, int n, out int[] positions, out int[] exps)
        {
            var posList = new List<int>();
            var expList = new List<int>();
            for (int e = 0; e < 255; e++)
            {
                byte x = GfExp(e);
                byte y = EvalLowHorner(sigma, x);
                if (y == 0)
                {
                    int k = (e + n - 1) % 255;
                    if (k >= 0 && k < n) { posList.Add(k); expList.Add(e); }
                }
            }
            positions = posList.ToArray();
            exps = expList.ToArray();
            return positions.Length == errCount;
        }

        // ---------------- small GF(256) polynomial helpers ----------------
        // (GfExpTable/GfLogTable/GfMul/GfExp/PolyMul are the encoder's own —
        // declared in QrCode.cs, shared here as private members of the same
        // partial class.)

        private static byte GfInverse(byte a)
        {
            // a^-1 = alpha^(255 - log(a)); GfExpTable[255] == GfExpTable[0] == 1,
            // so log(a)==0 (a==1) correctly yields inverse 1.
            return GfExpTable[255 - GfLogTable[a]];
        }

        /// <summary>Evaluates a HIGH-degree-first polynomial (poly[0] = highest degree term).</summary>
        private static byte EvalPolyHighFirst(byte[] poly, byte x)
        {
            byte y = poly[0];
            for (int i = 1; i < poly.Length; i++) y = (byte)(GfMul(y, x) ^ poly[i]);
            return y;
        }

        /// <summary>Evaluates a LOW-degree-first polynomial (coeffs[0] = constant term) via Horner.</summary>
        private static byte EvalLowHorner(byte[] coeffsLowFirst, byte x)
        {
            int d = coeffsLowFirst.Length - 1;
            byte y = coeffsLowFirst[d];
            for (int i = d - 1; i >= 0; i--) y = (byte)(GfMul(y, x) ^ coeffsLowFirst[i]);
            return y;
        }

        // ---------------- bit-stream parsing ----------------
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private int _pos;
            public BitReader(byte[] data) { _data = data; }
            public bool HasBits(int n) { return _pos + n <= _data.Length * 8; }
            public int ReadBits(int n)
            {
                int v = 0;
                for (int i = 0; i < n; i++)
                {
                    int bit = (_data[_pos >> 3] >> (7 - (_pos & 7))) & 1;
                    v = (v << 1) | bit;
                    _pos++;
                }
                return v;
            }
        }

        private const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

        /// <summary>
        /// Parses the decoded data codewords as a QR bit stream: mode
        /// indicator, length, payload, repeated until the terminator or data
        /// runs out. Byte mode is decoded as UTF-8 (matching the encoder, and
        /// what WireGuard configs are). Numeric/alphanumeric are supported for
        /// completeness; ECI and Kanji abort the decode (return whatever was
        /// accumulated so far, or null if nothing was decoded yet) rather than
        /// risk silently producing wrong text.
        /// </summary>
        private static string DecodeBitStream(byte[] data, int version)
        {
            var br = new BitReader(data);
            var sb = new System.Text.StringBuilder();

            while (br.HasBits(4))
            {
                int mode = br.ReadBits(4);
                if (mode == 0x0) break; // terminator

                if (mode == 0x4) // byte mode
                {
                    int lenBits = version <= 9 ? 8 : 16;
                    if (!br.HasBits(lenBits)) break;
                    int count = br.ReadBits(lenBits);
                    if (!br.HasBits(count * 8)) break;
                    byte[] bytes = new byte[count];
                    for (int i = 0; i < count; i++) bytes[i] = (byte)br.ReadBits(8);
                    sb.Append(System.Text.Encoding.UTF8.GetString(bytes));
                }
                else if (mode == 0x1) // numeric
                {
                    int lenBits = version <= 9 ? 10 : (version <= 26 ? 12 : 14);
                    if (!br.HasBits(lenBits)) break;
                    int count = br.ReadBits(lenBits);
                    while (count > 0)
                    {
                        if (count >= 3) { if (!br.HasBits(10)) return null; sb.Append(br.ReadBits(10).ToString("D3")); count -= 3; }
                        else if (count == 2) { if (!br.HasBits(7)) return null; sb.Append(br.ReadBits(7).ToString("D2")); count = 0; }
                        else { if (!br.HasBits(4)) return null; sb.Append(br.ReadBits(4).ToString("D1")); count = 0; }
                    }
                }
                else if (mode == 0x2) // alphanumeric
                {
                    int lenBits = version <= 9 ? 9 : (version <= 26 ? 11 : 13);
                    if (!br.HasBits(lenBits)) break;
                    int count = br.ReadBits(lenBits);
                    while (count >= 2)
                    {
                        if (!br.HasBits(11)) return null;
                        int v = br.ReadBits(11);
                        sb.Append(AlphanumericChars[v / 45]);
                        sb.Append(AlphanumericChars[v % 45]);
                        count -= 2;
                    }
                    if (count == 1)
                    {
                        if (!br.HasBits(6)) return null;
                        sb.Append(AlphanumericChars[br.ReadBits(6)]);
                    }
                }
                else if (mode == 0x7) // ECI designator
                {
                    // Many general-purpose QR generators prefix a byte-mode
                    // payload with an ECI designator (commonly ECI 26 = UTF-8)
                    // even for plain ASCII text. We don't need to interpret
                    // WHICH ECI it is — WireGuard config text is ASCII, a
                    // subset of UTF-8, so the byte-mode segment that follows
                    // decodes correctly via UTF8.GetString regardless. Just
                    // consume the designator's bits (1, 2, or 3 bytes per
                    // ISO/IEC 18004 Table 4, selected by the first byte's
                    // leading bit pattern) and continue to the next segment
                    // instead of aborting the whole decode over it.
                    if (!br.HasBits(8)) break;
                    int first = br.ReadBits(8);
                    if ((first & 0x80) == 0)
                    {
                        // 8-bit designator; already fully consumed.
                    }
                    else if ((first & 0xC0) == 0x80)
                    {
                        if (!br.HasBits(8)) break;
                        br.ReadBits(8);
                    }
                    else if ((first & 0xE0) == 0xC0)
                    {
                        if (!br.HasBits(16)) break;
                        br.ReadBits(16);
                    }
                    // Malformed designator (shouldn't happen): fall through
                    // and keep reading anyway rather than aborting.
                }
                else
                {
                    // Kanji (0x8) or anything else genuinely unhandled: stop
                    // rather than guess; return what we have so far if it's
                    // non-empty (covers a payload that started with byte mode
                    // and only switches modes partway through), else signal
                    // outright failure.
                    return sb.Length > 0 ? sb.ToString() : null;
                }
            }

            return sb.ToString();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace WgSharp.Ui
{
    /// <summary>
    /// Locates a QR code in a camera frame and samples its module grid, for
    /// QrCode.Decode to turn into text.
    ///
    /// Scope/limitations (read before relying on this for anything beyond
    /// "scan a tunnel QR off a phone/monitor held up to the webcam"):
    /// - Detection assumes the QR is reasonably flat and facing the camera.
    ///   It models the grid with an AFFINE transform (in-plane rotation is
    ///   handled fine; strong keystone/perspective skew is not) — full
    ///   perspective correction is out of scope here.
    /// - Binarization is a single global-average luminance threshold, not
    ///   adaptive — uneven lighting across the frame can hurt detection.
    /// - It does not know in advance which of the three found finder patterns
    ///   is top-left/top-right/bottom-left, or in which rotational sense, so
    ///   it brute-forces every assignment of the candidates it finds and lets
    ///   QrCode.Decode's own checks (format-info match, Reed-Solomon, bit
    ///   stream) reject the wrong ones — slightly wasteful, but removes an
    ///   entire class of orientation-math bugs in exchange for a few extra,
    ///   cheap decode attempts per frame.
    /// </summary>
    public static class QrImageLocator
    {
        /// <summary>
        /// Attempts to find and decode a QR code in the frame. Returns the
        /// decoded text, or null. <paramref name="diagnostic"/> gets a short,
        /// human-readable note on how far detection got before giving up
        /// (e.g. "found 2 candidate(s); need at least 3" vs. "found 3
        /// candidate(s); N orientation(s) sampled a plausible grid, none
        /// decoded") — shown in the scan dialog's status line so a failure
        /// report says something more useful than just "didn't work".
        /// </summary>
        public static string TryDecodeFrame(Bitmap frame, out string diagnostic)
        {
            diagnostic = null;
            bool[,] dark;
            int w, h;
            Binarize(frame, out dark, out w, out h);

            List<float[]> candidates = FindFinderCandidates(dark, w, h);
            if (candidates.Count < 3)
            {
                diagnostic = "Found " + candidates.Count + " finder-pattern candidate(s); need at least 3.";
                return null;
            }

            // Cap how many candidates we permute over, to bound worst-case cost
            // on a noisy/cluttered frame with many false-positive blobs. Safe
            // to keep this modest now that FindFinderCandidates sorts by
            // confidence (merged row-hit count) first — the real finders sort
            // to the top, so truncating no longer risks cutting them.
            int totalFound = candidates.Count;
            if (candidates.Count > 9) candidates = candidates.GetRange(0, 9);

            int n = candidates.Count;
            int gridsSampled = 0;
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++)
                {
                    if (b == a) continue;
                    for (int c = 0; c < n; c++)
                    {
                        if (c == a || c == b) continue;
                        bool[,] grid = SampleGrid(dark, w, h, candidates[a], candidates[b], candidates[c]);
                        if (grid == null) continue;
                        gridsSampled++;
                        string text = QrCode.Decode(grid);
                        if (text != null) return text;
                    }
                }
            }
            diagnostic = "Found " + totalFound + " finder-pattern candidate(s); " + gridsSampled +
                " orientation(s) sampled a plausible grid, but none decoded successfully " +
                "(format info, error correction, or bit-stream parsing failed on all of them).";
            return null;
        }

        /// <summary>Convenience overload without the diagnostic out-param.</summary>
        public static string TryDecodeFrame(Bitmap frame)
        {
            string diagnostic;
            return TryDecodeFrame(frame, out diagnostic);
        }

        // ---------------- binarization ----------------
        private static void Binarize(Bitmap bmp, out bool[,] dark, out int w, out int h)
        {
            w = bmp.Width;
            h = bmp.Height;
            dark = new bool[h, w];

            Bitmap working = bmp;
            bool ownCopy = false;
            if (bmp.PixelFormat != PixelFormat.Format24bppRgb)
            {
                working = bmp.Clone(new Rectangle(0, 0, w, h), PixelFormat.Format24bppRgb);
                ownCopy = true;
            }
            try
            {
                BitmapData bd = working.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    int stride = bd.Stride;
                    byte[] buffer = new byte[stride * h];
                    Marshal.Copy(bd.Scan0, buffer, 0, buffer.Length);

                    byte[] lum = new byte[w * h];
                    long sum = 0;
                    for (int y = 0; y < h; y++)
                    {
                        int rowOff = y * stride;
                        for (int x = 0; x < w; x++)
                        {
                            int p = rowOff + x * 3;
                            byte b = buffer[p], g = buffer[p + 1], r = buffer[p + 2];
                            byte l = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                            lum[y * w + x] = l;
                            sum += l;
                        }
                    }
                    byte avg = (byte)(sum / Math.Max(1, w * h));
                    for (int y = 0; y < h; y++)
                        for (int x = 0; x < w; x++)
                            dark[y, x] = lum[y * w + x] < avg;
                }
                finally { working.UnlockBits(bd); }
            }
            finally { if (ownCopy) working.Dispose(); }
        }

        // ---------------- finder pattern detection ----------------
        // Classic 1:1:3:1:1 dark/light run-length scan, horizontal pass with a
        // vertical cross-check, the same basic technique every QR scanner uses.
        private static List<float[]> FindFinderCandidates(bool[,] dark, int w, int h)
        {
            var raw = new List<float[]>(); // {x, y, moduleSizeEstimate}

            for (int y = 0; y < h; y++)
            {
                List<int> starts = new List<int>();
                List<int> lens = new List<int>();
                List<bool> colors = new List<bool>();
                int x = 0;
                while (x < w)
                {
                    bool color = dark[y, x];
                    int start = x;
                    while (x < w && dark[y, x] == color) x++;
                    starts.Add(start); lens.Add(x - start); colors.Add(color);
                }
                int n = lens.Count;
                for (int i = 0; i + 4 < n; i++)
                {
                    if (!(colors[i] && !colors[i + 1] && colors[i + 2] && !colors[i + 3] && colors[i + 4])) continue;
                    int l0 = lens[i], l1 = lens[i + 1], l2 = lens[i + 2], l3 = lens[i + 3], l4 = lens[i + 4];
                    float unit = (l0 + l1 + l3 + l4) / 4f;
                    if (unit < 1f) continue;
                    if (!InRange(l0, unit, 0.5f, 1.6f)) continue;
                    if (!InRange(l1, unit, 0.5f, 1.6f)) continue;
                    if (!InRange(l3, unit, 0.5f, 1.6f)) continue;
                    if (!InRange(l4, unit, 0.5f, 1.6f)) continue;
                    if (!InRange(l2, unit * 3f, 0.6f, 1.6f)) continue;
                    float centerX = starts[i + 2] + l2 / 2f;
                    raw.Add(new float[] { centerX, y, unit });
                }
            }

            // Merge radius widened from 2x to 3.5x the estimated module size:
            // a single real finder pattern's per-row hits can drift several
            // modules in x across its 7-module height under even modest
            // rotation, and 2x was fragmenting one real finder into several
            // separate clusters (seen in practice: 16 "candidates" reported
            // for a code with exactly 3 real finder patterns).
            List<float[]> clusters = ClusterPoints(raw, 3.5f);

            // Each cluster also carries `cnt` (how many raw row-hits merged
            // into it, element index 3) — kept through the vertical
            // cross-check as a confidence score. A real finder pattern is hit
            // by many rows (its full 7-module height) and clusters strongly;
            // a stray data-area false positive is typically hit by only one
            // or two rows. Carrying this lets the caller rank candidates by
            // confidence instead of by arbitrary scan order, so capping the
            // candidate list for the permutation search doesn't end up
            // discarding the real finders in favor of weak noise.

            var confirmed = new List<float[]>();
            foreach (float[] cl in clusters)
            {
                float refinedY;
                if (VerticalCrossCheck(dark, w, h, (int)Math.Round(cl[0]), (int)Math.Round(cl[1]), cl[2], out refinedY))
                    confirmed.Add(new float[] { cl[0], refinedY, cl[2], cl[3] }); // {x, y, moduleSize, confidence}
            }
            // Strongest (most row-hits) first, so a downstream cap on how
            // many candidates we permute keeps the real finders, not noise.
            confirmed.Sort(delegate(float[] a, float[] b) { return b[3].CompareTo(a[3]); });
            return confirmed;
        }

        private static bool VerticalCrossCheck(bool[,] dark, int w, int h, int x0, int y0, float unitEstimate, out float refinedY)
        {
            refinedY = y0;
            if (x0 < 0 || x0 >= w) return false;
            int searchHalf = (int)Math.Max(10, unitEstimate * 6);
            int yStart = Math.Max(0, y0 - searchHalf), yEnd = Math.Min(h - 1, y0 + searchHalf);

            List<int> starts = new List<int>();
            List<int> lens = new List<int>();
            List<bool> colors = new List<bool>();
            int y = yStart;
            while (y <= yEnd)
            {
                bool color = dark[y, x0];
                int start = y;
                while (y <= yEnd && dark[y, x0] == color) y++;
                starts.Add(start); lens.Add(y - start); colors.Add(color);
            }
            int n = lens.Count;
            float bestDist = float.MaxValue, bestCenter = -1;
            for (int i = 0; i + 4 < n; i++)
            {
                if (!(colors[i] && !colors[i + 1] && colors[i + 2] && !colors[i + 3] && colors[i + 4])) continue;
                int l0 = lens[i], l1 = lens[i + 1], l2 = lens[i + 2], l3 = lens[i + 3], l4 = lens[i + 4];
                float unit = (l0 + l1 + l3 + l4) / 4f;
                if (unit < 1f) continue;
                if (!InRange(l0, unit, 0.5f, 1.6f)) continue;
                if (!InRange(l1, unit, 0.5f, 1.6f)) continue;
                if (!InRange(l3, unit, 0.5f, 1.6f)) continue;
                if (!InRange(l4, unit, 0.5f, 1.6f)) continue;
                if (!InRange(l2, unit * 3f, 0.6f, 1.6f)) continue;
                float center = starts[i + 2] + l2 / 2f;
                float distFromOrig = Math.Abs(center - y0);
                if (distFromOrig < bestDist) { bestDist = distFromOrig; bestCenter = center; }
            }
            if (bestCenter < 0) return false;
            refinedY = bestCenter;
            return true;
        }

        private static bool InRange(float v, float refVal, float lo, float hi)
        {
            return v >= refVal * lo && v <= refVal * hi;
        }

        // Simple greedy single-pass clustering: good enough for grouping the
        // many per-row hits a single finder pattern produces into one point.
        // Returns {x, y, moduleSize, mergedCount} per cluster.
        private static List<float[]> ClusterPoints(List<float[]> pts, float radiusFactor)
        {
            var used = new bool[pts.Count];
            var result = new List<float[]>();
            for (int i = 0; i < pts.Count; i++)
            {
                if (used[i]) continue;
                float sx = pts[i][0], sy = pts[i][1], su = pts[i][2];
                int cnt = 1;
                used[i] = true;
                for (int j = i + 1; j < pts.Count; j++)
                {
                    if (used[j]) continue;
                    float dx = pts[j][0] - pts[i][0], dy = pts[j][1] - pts[i][1];
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= pts[i][2] * radiusFactor)
                    {
                        sx += pts[j][0]; sy += pts[j][1]; su += pts[j][2]; cnt++;
                        used[j] = true;
                    }
                }
                result.Add(new float[] { sx / cnt, sy / cnt, su / cnt, cnt });
            }
            return result;
        }

        // ---------------- grid sampling ----------------
        // Builds an affine basis from the three finder centers — pivot ("TL")
        // and the two others ("TR"/"BL", whichever way they actually are —
        // the class doc comment on why we don't bother determining that here
        // — and samples the module grid. Returns null on any geometry that's
        // clearly not a real QR (size out of range, sample point outside the
        // frame, wildly unequal side lengths).
        private static bool[,] SampleGrid(bool[,] dark, int w, int h, float[] tl, float[] tr, float[] bl)
        {
            float dxTR = tr[0] - tl[0], dyTR = tr[1] - tl[1];
            float dxBL = bl[0] - tl[0], dyBL = bl[1] - tl[1];
            float distTR = (float)Math.Sqrt(dxTR * dxTR + dyTR * dyTR);
            float distBL = (float)Math.Sqrt(dxBL * dxBL + dyBL * dyBL);
            if (distTR < 1f || distBL < 1f) return null;
            // A real QR's two finder-to-finder sides are equal length; reject
            // wildly unequal triples cheaply before doing a full grid sample.
            if (distBL < distTR * 0.4f || distBL > distTR * 2.5f) return null;

            float avgModulePx = (tl[2] + tr[2] + bl[2]) / 3f;
            if (avgModulePx < 0.5f) return null;

            float modulesAcross = distTR / avgModulePx; // ~= size - 7
            int sizeGuess = (int)Math.Round(modulesAcross) + 7;
            int k = (int)Math.Round((sizeGuess - 17) / 4.0);
            if (k < 1) k = 1;
            if (k > 40) k = 40;
            int size = 17 + 4 * k;
            int modulesPerSide = size - 7;

            float xAxisX = dxTR / modulesPerSide, xAxisY = dyTR / modulesPerSide;
            float yAxisX = dxBL / modulesPerSide, yAxisY = dyBL / modulesPerSide;

            // origin = pixel position of module (0,0)'s corner. The finder
            // pattern's own center sits at module coordinate (3.5, 3.5) from
            // that corner along each axis (see QrCode.DrawFunctionPatterns:
            // finder eyes are centered at module index 3 on a 7-wide pattern).
            float originX = tl[0] - 3.5f * xAxisX - 3.5f * yAxisX;
            float originY = tl[1] - 3.5f * xAxisY - 3.5f * yAxisY;

            bool[,] grid = new bool[size, size];
            for (int r = 0; r < size; r++)
            {
                for (int c = 0; c < size; c++)
                {
                    float px = originX + (c + 0.5f) * xAxisX + (r + 0.5f) * yAxisX;
                    float py = originY + (c + 0.5f) * xAxisY + (r + 0.5f) * yAxisY;
                    int ix = (int)Math.Round(px), iy = (int)Math.Round(py);
                    if (ix < 0 || iy < 0 || ix >= w || iy >= h) return null;
                    grid[r, c] = dark[iy, ix];
                }
            }
            return grid;
        }
    }
}

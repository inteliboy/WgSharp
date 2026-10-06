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
    /// - The grid is modelled with an AFFINE transform from the three finder
    ///   centers (in-plane rotation is fine), then, for version 2+ codes, a
    ///   PERSPECTIVE homography that adds the bottom-right alignment pattern
    ///   as a fourth control point. The affine grid remains as a fallback.
    /// - Binarization tries an adaptive local-mean threshold first, then a
    ///   single global-average threshold as a fallback.
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
            // Adaptive (local-mean) threshold first: it survives uneven exposure,
            // glare gradients and a bright phone screen in a dim room, which is what
            // a hand-held webcam scan actually looks like. The global-mean threshold
            // stays as a fallback since it is cleaner on flat, evenly lit images
            // (screenshots, scanned files).
            string text = TryDecodeBinarized(frame, true, false, out diagnostic);
            if (text != null) return text;
            string other;
            text = TryDecodeBinarized(frame, false, false, out other);
            if (text != null) { diagnostic = null; return text; }
            // Light-on-dark QR (some phone dark themes render it inverted): only worth the
            // extra pass when the normal ones found nothing usable.
            text = TryDecodeBinarized(frame, true, true, out other);
            if (text != null) { diagnostic = null; return text; }
            return null;
        }

        private static string TryDecodeBinarized(Bitmap frame, bool adaptive, bool invert, out string diagnostic)
        {
            diagnostic = null;
            bool[,] dark;
            int w, h;
            Binarize(frame, adaptive, out dark, out w, out h);
            if (invert)
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                        dark[y, x] = !dark[y, x];

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
                        foreach (bool[,] grid in SampleGrids(dark, w, h, candidates[a], candidates[b], candidates[c]))
                        {
                            gridsSampled++;
                            string text = QrCode.Decode(grid);
                            if (text != null) return text;
                        }
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
        private static void Binarize(Bitmap bmp, bool adaptive, out bool[,] dark, out int w, out int h)
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
                    if (!adaptive)
                    {
                        byte avg = (byte)(sum / Math.Max(1, w * h));
                        for (int y = 0; y < h; y++)
                            for (int x = 0; x < w; x++)
                                dark[y, x] = lum[y * w + x] < avg;
                    }
                    else
                    {
                        // Local mean over a window ~1/8 of the short side (wider than a
                        // finder pattern at typical scan distances), via an integral image
                        // so cost is O(pixels). A pixel is dark if it is clearly below its
                        // neighbourhood mean; the 8% margin keeps sensor noise in flat
                        // regions from turning into speckle.
                        int win = Math.Max(15, Math.Min(w, h) / 8) | 1;
                        int r = win / 2;
                        int iw = w + 1;
                        long[] integral = new long[iw * (h + 1)];
                        for (int y = 0; y < h; y++)
                        {
                            long rowSum = 0;
                            for (int x = 0; x < w; x++)
                            {
                                rowSum += lum[y * w + x];
                                integral[(y + 1) * iw + x + 1] = integral[y * iw + x + 1] + rowSum;
                            }
                        }
                        for (int y = 0; y < h; y++)
                        {
                            int y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
                            for (int x = 0; x < w; x++)
                            {
                                int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1);
                                long area = (long)(x1 - x0) * (y1 - y0);
                                long s = integral[y1 * iw + x1] - integral[y0 * iw + x1]
                                       - integral[y1 * iw + x0] + integral[y0 * iw + x0];
                                long v = (long)lum[y * w + x] * area;
                                // clearly below the local mean, both relatively (8%) and
                                // absolutely (8 levels) so noise in flat or dark areas
                                // doesn't become speckle
                                dark[y, x] = v * 100 < s * 92 && v + 8 * area < s;
                            }
                        }
                    }
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
                if (!VerticalCrossCheck(dark, w, h, (int)Math.Round(cl[0]), (int)Math.Round(cl[1]), cl[2], out refinedY))
                    continue;

                // The X estimate up to this point is just the raw-hit
                // cluster centroid from the horizontal scan pass - under
                // rotation, different scanlines cross a tilted finder
                // pattern at different apparent X positions, and naively
                // averaging them (ClusterPoints) doesn't reliably land on
                // the true center (confirmed via a synthetic round-trip
                // test: one finder's X came out ~1.4 modules off, enough by
                // itself to misalign the whole affine grid). Symmetric fix:
                // now that Y is refined, cross-check back along THAT row to
                // refine X the same way VerticalCrossCheck refined Y.
                float refinedX;
                if (!HorizontalCrossCheck(dark, w, h, (int)Math.Round(refinedY), (int)Math.Round(cl[0]), cl[2], out refinedX))
                    refinedX = cl[0]; // fall back to the centroid rather than dropping an otherwise-valid candidate

                confirmed.Add(new float[] { refinedX, refinedY, cl[2], cl[3] }); // {x, y, moduleSize, confidence}
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

        // Mirrors VerticalCrossCheck exactly, swapping axes: scans row y0
        // instead of column x0, to refine an X estimate. See the call site's
        // comment in FindFinderCandidates for why a single horizontal pass
        // alone (with no reciprocal horizontal check after Y is refined)
        // isn't enough under rotation.
        private static bool HorizontalCrossCheck(bool[,] dark, int w, int h, int y0, int x0, float unitEstimate, out float refinedX)
        {
            refinedX = x0;
            if (y0 < 0 || y0 >= h) return false;
            int searchHalf = (int)Math.Max(10, unitEstimate * 6);
            int xStart = Math.Max(0, x0 - searchHalf), xEnd = Math.Min(w - 1, x0 + searchHalf);

            List<int> starts = new List<int>();
            List<int> lens = new List<int>();
            List<bool> colors = new List<bool>();
            int x = xStart;
            while (x <= xEnd)
            {
                bool color = dark[y0, x];
                int start = x;
                while (x <= xEnd && dark[y0, x] == color) x++;
                starts.Add(start); lens.Add(x - start); colors.Add(color);
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
                float distFromOrig = Math.Abs(center - x0);
                if (distFromOrig < bestDist) { bestDist = distFromOrig; bestCenter = center; }
            }
            if (bestCenter < 0) return false;
            refinedX = bestCenter;
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
        // — and samples the module grid at each plausible size (see
        // SizeCandidates). Returns no grids at all on geometry that's clearly
        // not a real QR (size out of range, sample point outside the frame,
        // wildly unequal side lengths).
        //
        // Sizing itself is the fiddly part: the per-finder "unit" (module
        // pixel size) that SizeCandidates' caller would otherwise divide by
        // is measured along a horizontal/vertical scanline through the
        // pattern (FindFinderCandidates/VerticalCrossCheck) — for an
        // in-plane-ROTATED code, that scanline cuts across the tilted
        // pattern at an angle, so it overestimates the true edge-to-edge
        // module size (by roughly 1/cos(rotation), a few percent even for a
        // modest few-degree tilt someone holding up a phone will easily
        // produce). That bias, combined with the size formula's rounding
        // landing near a version-boundary, was previously enough to guess
        // one whole QR version off - which is fatal (every fixed-position
        // element - format info, alignment/timing patterns, the codeword
        // zigzag - reads from the wrong place, so no amount of Reed-Solomon
        // correction downstream can recover it), even though the actual
        // finder-triple geometry was completely correct. Confirmed via a
        // synthetic round-trip test (encode -> render -> rotate/resize ->
        // decode): forcing the correct size decoded successfully every time
        // the single best-guess size failed.
        private static IEnumerable<bool[,]> SampleGrids(bool[,] dark, int w, int h, float[] tl, float[] tr, float[] bl)
        {
            float dxTR = tr[0] - tl[0], dyTR = tr[1] - tl[1];
            float dxBL = bl[0] - tl[0], dyBL = bl[1] - tl[1];
            float distTR = (float)Math.Sqrt(dxTR * dxTR + dyTR * dyTR);
            float distBL = (float)Math.Sqrt(dxBL * dxBL + dyBL * dyBL);
            if (distTR < 1f || distBL < 1f) yield break;
            // A real QR's two finder-to-finder sides are equal length; reject
            // wildly unequal triples cheaply before doing a full grid sample.
            if (distBL < distTR * 0.4f || distBL > distTR * 2.5f) yield break;
            // Real finder triple: right angle at tl (loosened for perspective) and the
            // right handedness of an unmirrored image (tr clockwise of bl in y-down
            // pixel space). Cuts the brute-forced permutations to the few that can be real.
            float cross = dxTR * dyBL - dyTR * dxBL;
            if (cross <= 0f) yield break;
            float cosAngle = (dxTR * dxBL + dyTR * dyBL) / (distTR * distBL);
            if (Math.Abs(cosAngle) > 0.4f) yield break;

            float avgModulePx = (tl[2] + tr[2] + bl[2]) / 3f;
            if (avgModulePx < 0.5f) yield break;

            // Mean of both sides: under perspective one side is foreshortened, which would
            // bias the module count (and so the whole version guess) low.
            foreach (int size in SizeCandidates((distTR + distBL) / 2f, avgModulePx))
            {
                int modulesPerSide = size - 7;
                float xAxisX = dxTR / modulesPerSide, xAxisY = dyTR / modulesPerSide;
                float yAxisX = dxBL / modulesPerSide, yAxisY = dyBL / modulesPerSide;
                // Vote over ~40% of the module's width around its center instead of reading one
                // pixel: webcam noise and a phone screen's own pixel texture flip lone pixels
                // (measured: ~7% of modules misread on a sharp frame, far past what
                // Reed-Solomon can repair).
                float modPx = Math.Min((float)Math.Sqrt(xAxisX * xAxisX + xAxisY * xAxisY),
                                       (float)Math.Sqrt(yAxisX * yAxisX + yAxisY * yAxisY));
                int voteRadius = modPx >= 3f ? (int)Math.Round(modPx * 0.2f) : 0;

                // origin = pixel position of module (0,0)'s corner. The finder
                // pattern's own center sits at module coordinate (3.5, 3.5)
                // from that corner along each axis (see
                // QrCode.DrawFunctionPatterns: finder eyes are centered at
                // module index 3 on a 7-wide pattern).
                float originX = tl[0] - 3.5f * xAxisX - 3.5f * yAxisX;
                float originY = tl[1] - 3.5f * xAxisY - 3.5f * yAxisY;

                bool[,] grid = new bool[size, size];
                bool outOfBounds = false;
                for (int r = 0; r < size && !outOfBounds; r++)
                {
                    for (int c = 0; c < size; c++)
                    {
                        float px = originX + (c + 0.5f) * xAxisX + (r + 0.5f) * yAxisX;
                        float py = originY + (c + 0.5f) * xAxisY + (r + 0.5f) * yAxisY;
                        int ix = (int)Math.Round(px), iy = (int)Math.Round(py);
                        if (ix < 0 || iy < 0 || ix >= w || iy >= h) { outOfBounds = true; break; }
                        grid[r, c] = SampleModule(dark, w, h, ix, iy, voteRadius);
                    }
                }
                // Perspective-corrected grid first (when the 4th control point, the
                // bottom-right alignment pattern, can be found), the affine grid after.
                // The affine model puts the far corner several modules off under even
                // mild keystone on a dense code - fatal at version 10+ - while the
                // alignment pattern pins that corner down.
                if (size >= 25)
                {
                    float ac = size - 6.5f;
                    float predX = originX + ac * xAxisX + ac * yAxisX;
                    float predY = originY + ac * xAxisY + ac * yAxisY;
                    float axf, ayf;
                    if (FindAlignmentCenter(dark, w, h, predX, predY, xAxisX, xAxisY, yAxisX, yAxisY, out axf, out ayf))
                    {
                        double[] hm = SolveHomography(
                            new double[] { 3.5, size - 3.5, 3.5, ac },
                            new double[] { 3.5, 3.5, size - 3.5, ac },
                            new double[] { tl[0], tr[0], bl[0], axf },
                            new double[] { tl[1], tr[1], bl[1], ayf });
                        if (hm != null)
                        {
                            bool[,] pg = new bool[size, size];
                            bool pgOut = false;
                            for (int r = 0; r < size && !pgOut; r++)
                            {
                                for (int c = 0; c < size; c++)
                                {
                                    double u = c + 0.5, v = r + 0.5;
                                    double den = hm[6] * u + hm[7] * v + 1.0;
                                    if (Math.Abs(den) < 1e-9) { pgOut = true; break; }
                                    int ix = (int)Math.Round((hm[0] * u + hm[1] * v + hm[2]) / den);
                                    int iy = (int)Math.Round((hm[3] * u + hm[4] * v + hm[5]) / den);
                                    if (ix < 0 || iy < 0 || ix >= w || iy >= h) { pgOut = true; break; }
                                    pg[r, c] = SampleModule(dark, w, h, ix, iy, voteRadius);
                                }
                            }
                            if (!pgOut) yield return pg;
                        }
                    }
                }
                if (!outOfBounds) yield return grid;
            }
        }

        // Majority vote of the binarized pixels in a (2r+1)^2 square around (x,y).
        private static bool SampleModule(bool[,] dark, int w, int h, int x, int y, int r)
        {
            if (r <= 0) return dark[y, x];
            int count = 0, total = 0;
            for (int yy = Math.Max(0, y - r); yy <= Math.Min(h - 1, y + r); yy++)
                for (int xx = Math.Max(0, x - r); xx <= Math.Min(w - 1, x + r); xx++)
                { total++; if (dark[yy, xx]) count++; }
            return count * 2 > total;
        }

        // Finds the center of the bottom-right alignment pattern (5x5 modules: dark
        // ring, light ring, dark center) near its affine-predicted position. Under real
        // perspective the affine prediction can be many modules off (one hand-held
        // phone frame measured ~10), so this searches +-14 modules: a coarse template
        // match first, then a 1px refinement around the winner. Needs a near-perfect
        // match (>= 22 of 25 cells) so random texture can't masquerade as one; among
        // equally good matches the one nearest the prediction wins.
        private static bool FindAlignmentCenter(bool[,] dark, int w, int h, float predX, float predY,
            float xAx, float xAy, float yAx, float yAy, out float cx, out float cy)
        {
            cx = cy = 0;
            float mod = (float)Math.Sqrt(xAx * xAx + xAy * xAy);
            int range = (int)Math.Ceiling(14f * mod);
            int step = Math.Max(2, (int)(mod / 4f));
            int best = 0; float bx0 = 0, by0 = 0; float bestDist = float.MaxValue;
            for (int dy = -range; dy <= range; dy += step)
            {
                for (int dx = -range; dx <= range; dx += step)
                {
                    int score = AlignmentScore(dark, w, h, predX + dx, predY + dy, xAx, xAy, yAx, yAy);
                    if (score < 0) continue;
                    float d = dx * dx + dy * dy;
                    if (score > best || (score == best && d < bestDist))
                    { best = score; bx0 = predX + dx; by0 = predY + dy; bestDist = d; }
                }
            }
            if (best < 17) return false; // coarse grid may straddle the optimum; refine below
            int fine = best = 0; double sx = 0, sy = 0; int n = 0;
            int r2 = step + 1;
            for (int dy = -r2; dy <= r2; dy++)
            {
                for (int dx = -r2; dx <= r2; dx++)
                {
                    int score = AlignmentScore(dark, w, h, bx0 + dx, by0 + dy, xAx, xAy, yAx, yAy);
                    if (score > fine) { fine = score; sx = bx0 + dx; sy = by0 + dy; n = 1; }
                    else if (score == fine && score >= 0) { sx += bx0 + dx; sy += by0 + dy; n++; }
                }
            }
            if (fine < 22) return false;
            cx = (float)(sx / n); cy = (float)(sy / n);
            return true;
        }

        // Cells of the 5x5 alignment template (center dark, ring 1 light, ring 2 dark)
        // that match the binarized image when centered at (bx,by); -1 if off-frame.
        private static int AlignmentScore(bool[,] dark, int w, int h, float bx, float by,
            float xAx, float xAy, float yAx, float yAy)
        {
            int score = 0;
            for (int j = -2; j <= 2; j++)
            {
                for (int i = -2; i <= 2; i++)
                {
                    int ix = (int)Math.Round(bx + i * xAx + j * yAx);
                    int iy = (int)Math.Round(by + i * xAy + j * yAy);
                    if (ix < 0 || iy < 0 || ix >= w || iy >= h) return -1;
                    bool expectDark = Math.Max(Math.Abs(i), Math.Abs(j)) != 1;
                    if (dark[iy, ix] == expectDark) score++;
                }
            }
            return score;
        }

        // Homography from module coordinates (u,v) to pixels (x,y) through four point
        // pairs: x = (a u + b v + c) / (g u + h v + 1), y = (d u + e v + f) / (g u + h v + 1).
        // Returns {a,b,c,d,e,f,g,h}, or null if the points are degenerate.
        private static double[] SolveHomography(double[] u, double[] v, double[] x, double[] y)
        {
            double[,] m = new double[8, 9];
            for (int i = 0; i < 4; i++)
            {
                int r = i * 2;
                m[r, 0] = u[i]; m[r, 1] = v[i]; m[r, 2] = 1; m[r, 6] = -u[i] * x[i]; m[r, 7] = -v[i] * x[i]; m[r, 8] = x[i];
                m[r + 1, 3] = u[i]; m[r + 1, 4] = v[i]; m[r + 1, 5] = 1; m[r + 1, 6] = -u[i] * y[i]; m[r + 1, 7] = -v[i] * y[i]; m[r + 1, 8] = y[i];
            }
            for (int col = 0; col < 8; col++)
            {
                int piv = col;
                for (int r = col + 1; r < 8; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
                if (Math.Abs(m[piv, col]) < 1e-12) return null;
                if (piv != col)
                    for (int k = 0; k < 9; k++) { double t = m[col, k]; m[col, k] = m[piv, k]; m[piv, k] = t; }
                for (int r = 0; r < 8; r++)
                {
                    if (r == col) continue;
                    double f = m[r, col] / m[col, col];
                    if (f == 0) continue;
                    for (int k = col; k < 9; k++) m[r, k] -= f * m[col, k];
                }
            }
            double[] res = new double[8];
            for (int i = 0; i < 8; i++) res[i] = m[i, 8] / m[i, i];
            return res;
        }

        // A small window of plausible QR sizes (21, 25, 29, ... 177) around
        // the geometry's best estimate, instead of committing to a single
        // rounded guess - see SampleGrids' comment for why the single-guess
        // version was fragile under rotation. Tries the best guess first
        // (the common, unrotated/well-aligned case still resolves on the
        // first attempt), then its immediate smaller/larger neighbors.
        private static IEnumerable<int> SizeCandidates(float distTR, float avgModulePx)
        {
            float modulesAcross = distTR / avgModulePx; // ~= size - 7
            int sizeGuess = (int)Math.Round(modulesAcross) + 7;
            int kGuess = (int)Math.Round((sizeGuess - 17) / 4.0);

            int[] offsets = { 0, -1, 1 };
            var seen = new HashSet<int>();
            foreach (int off in offsets)
            {
                int k = kGuess + off;
                if (k < 1) k = 1;
                if (k > 40) k = 40;
                if (seen.Add(k)) yield return 17 + 4 * k;
            }
        }
    }
}

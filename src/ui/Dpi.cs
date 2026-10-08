using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    // Pixel scaling for a DPI-aware process. app.manifest declares the process
    // system-DPI-aware, so Windows no longer bitmap-stretches the UI (the old
    // source of blurry text) - the flip side is that every hardcoded pixel
    // value in the layout code has to be scaled by hand. Forms here are built
    // in code with AutoScaleMode.None, so this is the single source of truth:
    // write layout numbers at 96 DPI and wrap them in Dpi.S(...).
    internal static class Dpi
    {
        private const int LOGPIXELSX = 88;
        private static float _scale;

        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] private static extern int GetDeviceCaps(IntPtr hDC, int index);

        /// <summary>System DPI divided by 96 (1.0 at 100%, 1.5 at 150%, ...).</summary>
        public static float Scale
        {
            get
            {
                if (_scale <= 0f)
                {
                    int dpi = 96;
                    IntPtr dc = GetDC(IntPtr.Zero);
                    if (dc != IntPtr.Zero)
                    {
                        try { dpi = GetDeviceCaps(dc, LOGPIXELSX); }
                        finally { ReleaseDC(IntPtr.Zero, dc); }
                    }
                    _scale = dpi > 0 ? dpi / 96f : 1f;
                }
                return _scale;
            }
        }

        public static int S(int px) { return (int)Math.Round(px * Scale); }
        public static Size S(Size s) { return new Size(S(s.Width), S(s.Height)); }
        public static Size Sz(int w, int h) { return new Size(S(w), S(h)); }
        public static Point Pt(int x, int y) { return new Point(S(x), S(y)); }
        public static Padding Pad(int all) { return new Padding(S(all)); }
        public static Padding Pad(int l, int t, int r, int b) { return new Padding(S(l), S(t), S(r), S(b)); }
        public static Padding S(Padding p) { return new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom)); }
    }
}

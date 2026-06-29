using System.Drawing;
using System.Drawing.Drawing2D;

namespace WgSharp.Ui
{
    /// <summary>
    /// Small vector-style icons rendered to bitmaps for the toolbar buttons, so
    /// the build needs no embedded image resources. Colors echo the official app
    /// (green add, red delete, neutral export/QR).
    /// </summary>
    internal static class Icons
    {
        public static Bitmap Add(int size)
        {
            var bmp = NewBitmap(size);
            using (var g = Graphics.FromImage(bmp))
            {
                Prep(g);
                var green = Color.FromArgb(0x3C, 0x9A, 0x40);
                using (var pen = new Pen(green, System.Math.Max(2f, size / 8f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    float c = size / 2f, r = size * 0.30f;
                    g.DrawLine(pen, c - r, c, c + r, c);
                    g.DrawLine(pen, c, c - r, c, c + r);
                }
            }
            return bmp;
        }

        public static Bitmap Delete(int size)
        {
            var bmp = NewBitmap(size);
            using (var g = Graphics.FromImage(bmp))
            {
                Prep(g);
                var red = Color.FromArgb(0xC0, 0x39, 0x2B);
                using (var pen = new Pen(red, System.Math.Max(2f, size / 8f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    float c = size / 2f, r = size * 0.26f;
                    g.DrawLine(pen, c - r, c - r, c + r, c + r);
                    g.DrawLine(pen, c + r, c - r, c - r, c + r);
                }
            }
            return bmp;
        }

        public static Bitmap Export(int size)
        {
            // a downward arrow into a tray (export to file)
            var bmp = NewBitmap(size);
            using (var g = Graphics.FromImage(bmp))
            {
                Prep(g);
                var col = Color.FromArgb(0x55, 0x55, 0x55);
                using (var pen = new Pen(col, System.Math.Max(1.6f, size / 10f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    float c = size / 2f;
                    g.DrawLine(pen, c, size * 0.22f, c, size * 0.58f);
                    g.DrawLines(pen, new[]
                    {
                        new PointF(c - size * 0.16f, size * 0.42f),
                        new PointF(c, size * 0.60f),
                        new PointF(c + size * 0.16f, size * 0.42f)
                    });
                    g.DrawLine(pen, size * 0.26f, size * 0.74f, size * 0.74f, size * 0.74f);
                }
            }
            return bmp;
        }

        public static Bitmap QrGlyph(int size)
        {
            // a tiny stylized QR corner motif
            var bmp = NewBitmap(size);
            using (var g = Graphics.FromImage(bmp))
            {
                Prep(g);
                var col = Color.FromArgb(0x44, 0x44, 0x44);
                using (var b = new SolidBrush(col))
                {
                    float u = size * 0.16f;
                    // three finder squares
                    DrawFinder(g, b, u, u, u);
                    DrawFinder(g, b, size - u * 3.0f, u, u);
                    DrawFinder(g, b, u, size - u * 3.0f, u);
                    // a couple of data dots
                    g.FillRectangle(b, size * 0.55f, size * 0.55f, u * 0.8f, u * 0.8f);
                    g.FillRectangle(b, size * 0.72f, size * 0.70f, u * 0.8f, u * 0.8f);
                }
            }
            return bmp;
        }

        private static void DrawFinder(Graphics g, Brush b, float x, float y, float u)
        {
            g.FillRectangle(b, x, y, u * 2f, u * 2f);
            using (var hole = new SolidBrush(Color.White))
                g.FillRectangle(hole, x + u * 0.5f, y + u * 0.5f, u, u);
        }

        private static Bitmap NewBitmap(int size)
        {
            var bmp = new Bitmap(size, size);
            bmp.MakeTransparent();
            return bmp;
        }

        private static void Prep(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
        }
    }
}

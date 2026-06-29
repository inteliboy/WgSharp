using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// The "Status" value: a small colored shield (green when active, amber while
    /// connecting, grey when inactive) followed by the status text, matching the
    /// official app's Interface status line.
    /// </summary>
    public sealed class StatusRow : Control
    {
        private string _text = "Inactive";
        private Color _color = Color.FromArgb(0xB0, 0xB0, 0xB0);
        private bool _active;

        public StatusRow()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Height = 18;
        }

        public void Set(string text, Color color, bool active)
        {
            _text = text; _color = color; _active = active;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int y = (Height - 14) / 2;
            DrawShield(g, 0, y, _active, _color);

            using (var b = new SolidBrush(ForeColor))
                g.DrawString(_text, Font, b, 20, (Height - Font.Height) / 2f);
        }

        internal static void DrawShield(Graphics g, int x, int y, bool active, Color color)
        {
            float w = 14, h = 14, cx = x + w / 2f;
            using (var path = new GraphicsPath())
            {
                path.AddLine(cx, y + 0.5f, x + w - 1.5f, y + 2.5f);
                path.AddLine(x + w - 1.5f, y + 2.5f, x + w - 1.5f, y + h * 0.55f);
                path.AddBezier(x + w - 1.5f, y + h * 0.55f, x + w - 1.5f, y + h * 0.8f, cx, y + h - 0.5f, cx, y + h - 0.5f);
                path.AddBezier(cx, y + h - 0.5f, cx, y + h - 0.5f, x + 1.5f, y + h * 0.8f, x + 1.5f, y + h * 0.55f);
                path.AddLine(x + 1.5f, y + h * 0.55f, x + 1.5f, y + 2.5f);
                path.AddLine(x + 1.5f, y + 2.5f, cx, y + 0.5f);
                path.CloseFigure();
                using (var b = new SolidBrush(color)) g.FillPath(b, path);
                if (active)
                    using (var p = new Pen(Color.White, 1.5f))
                    {
                        p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                        g.DrawLines(p, new[]
                        {
                            new PointF(x + w * 0.30f, y + h * 0.52f),
                            new PointF(x + w * 0.44f, y + h * 0.66f),
                            new PointF(x + w * 0.70f, y + h * 0.34f)
                        });
                    }
            }
        }
    }
}

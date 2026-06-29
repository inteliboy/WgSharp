using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// A small live area chart in the spirit of OpenWrt/LuCI's RRDtool graphs:
    /// a scrolling filled series with a grid, border, title, and a current/avg/max
    /// legend. Feed it samples with Push(); it keeps a fixed-width history.
    /// </summary>
    public sealed class AreaChart : Control
    {
        private readonly Queue<double> _data = new Queue<double>();
        private int _capacity = 120;       // samples kept (e.g. 120s of history)
        private string _title = "";
        private string _unit = "";
        private Color _line = Color.FromArgb(0x3A, 0x6E, 0xA5);
        private Func<double, string> _fmt;

        public AreaChart()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
            _fmt = DefaultFormat;
            Height = 120;
        }

        public void Configure(string title, string unit, Color line, int capacity, Func<double, string> fmt)
        {
            _title = title; _unit = unit; _line = line; _capacity = capacity;
            if (fmt != null) _fmt = fmt;
            Invalidate();
        }

        public void Push(double value)
        {
            if (value < 0) value = 0;
            _data.Enqueue(value);
            while (_data.Count > _capacity) _data.Dequeue();
            Invalidate();
        }

        public void Reset() { _data.Clear(); Invalidate(); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Invalidate();
        }

        private string DefaultFormat(double v) { return v.ToString("0.0") + " " + _unit; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.White);

            // Symmetric outer margins: the plot is inset the SAME amount on the
            // left and the right edge of the control, so the chart sits evenly
            // within the Stats tab (the right side used to look tighter because
            // the y-axis scale labels were drawn OUTSIDE the plot, spilling into
            // the right margin). Now we reserve a label gutter INSIDE that right
            // margin, between the plot's right border and the outer edge, so the
            // labels fit within the control and both gutters to the window match.
            const int outerMargin = 8;      // gap from control edge to chart, both sides
            const int labelGutter = 46;     // room for the y-axis scale text, inside the right margin
            int plotLeft = outerMargin;
            int plotRight = Width - outerMargin - labelGutter;
            var plot = new Rectangle(plotLeft, 22, Math.Max(10, plotRight - plotLeft), Height - 58);

            // Plot border (light box on all four sides).
            using (var p = new Pen(Color.FromArgb(0xCF, 0xD4, 0xDA)))
                g.DrawRectangle(p, plot);
            // A clearly visible vertical axis line on the LEFT edge of the plot,
            // heavier and darker than the light box, so the chart reads as a
            // proper graph with a defined left boundary.
            using (var axis = new Pen(Color.FromArgb(0x8A, 0x93, 0x9C), 1.6f))
                g.DrawLine(axis, plot.Left, plot.Top, plot.Left, plot.Bottom);

            // title
            using (var tb = new SolidBrush(AppTheme.GroupText))
            using (var tf = new Font("Segoe UI", 8.5F, FontStyle.Bold))
                g.DrawString(_title, tf, tb, plot.Left, 4);

            double[] vals = _data.ToArray();
            double max = 1;
            foreach (double v in vals) if (v > max) max = v;
            // round max up for a nicer scale
            max = NiceCeil(max);

            // grid lines (4 horizontal). Scale labels sit in the reserved
            // gutter just past the plot's right border — which is now inside
            // the control, not spilling toward the window edge.
            using (var gp = new Pen(Color.FromArgb(0xEC, 0xEF, 0xF2)))
            using (var lf = new Font("Segoe UI", 7F))
            using (var lb = new SolidBrush(AppTheme.FieldLabel))
            {
                for (int i = 0; i <= 4; i++)
                {
                    int y = plot.Top + plot.Height * i / 4;
                    g.DrawLine(gp, plot.Left, y, plot.Right, y);
                    double gv = max * (4 - i) / 4.0;
                    g.DrawString(_fmt(gv), lf, lb, plot.Right + 4, y - 6);
                }
            }

            // area + line
            if (vals.Length >= 2)
            {
                var pts = new PointF[vals.Length];
                for (int i = 0; i < vals.Length; i++)
                {
                    float x = plot.Left + (float)plot.Width * i / (_capacity - 1);
                    float y = plot.Bottom - (float)(vals[i] / max) * plot.Height;
                    pts[i] = new PointF(x, y);
                }
                // fill
                var fillPts = new List<PointF>(pts);
                fillPts.Add(new PointF(pts[pts.Length - 1].X, plot.Bottom));
                fillPts.Add(new PointF(pts[0].X, plot.Bottom));
                using (var path = new GraphicsPath())
                {
                    path.AddLines(fillPts.ToArray());
                    using (var b = new SolidBrush(Color.FromArgb(64, _line)))
                        g.FillPath(b, path);
                }
                using (var lp = new Pen(_line, 1.4f))
                    g.DrawLines(lp, pts);
            }

            // legend: current / avg / max
            double cur = vals.Length > 0 ? vals[vals.Length - 1] : 0;
            double avg = 0; foreach (double v in vals) avg += v; if (vals.Length > 0) avg /= vals.Length;
            double mx = 0; foreach (double v in vals) if (v > mx) mx = v;

            using (var lf = new Font("Consolas", 8F))
            using (var sw = new SolidBrush(_line))
            {
                int ly = plot.Bottom + 8;
                g.FillRectangle(sw, plot.Left, ly + 2, 9, 9);
                using (var tb = new SolidBrush(AppTheme.FieldValue))
                    g.DrawString("Cur " + _fmt(cur) + "    Avg " + _fmt(avg) + "    Max " + _fmt(mx),
                        lf, tb, plot.Left + 14, ly);
            }
        }

        private static double NiceCeil(double v)
        {
            if (v <= 1) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double n = v / mag;
            double nice;
            if (n <= 1) nice = 1; else if (n <= 2) nice = 2; else if (n <= 5) nice = 5; else nice = 10;
            return nice * mag;
        }
    }
}

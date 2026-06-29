using System;
using System.Drawing;
using System.Windows.Forms;
using WgSharp.Core;

namespace WgSharp.Ui
{
    /// <summary>
    /// The Stats tab: live upload/download rate charts plus readouts for total
    /// transferred, session duration, and peer latency. Fed once per second from
    /// the form's status tick via Update().
    /// </summary>
    public sealed class StatsPanel : Panel
    {
        private readonly AreaChart _downChart;
        private readonly AreaChart _upChart;
        private readonly Label _lblDown, _lblUp, _lblTotal, _lblDuration, _lblLatency;

        private long _prevRx = -1, _prevTx = -1;
        private DateTime _prevTime = DateTime.MinValue;
        private DateTime _sessionStart = DateTime.MinValue;

        public StatsPanel()
        {
            Dock = DockStyle.Fill;
            AutoScroll = true;
            BackColor = AppTheme.PanelBg;
            Padding = new Padding(6);

            _downChart = new AreaChart();
            _downChart.Configure("Download rate", "B/s", Color.FromArgb(0x3A, 0x6E, 0xA5), 120, FmtRate);
            _downChart.Location = new Point(8, 8);
            _downChart.Size = new Size(440, 130);
            _downChart.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            _upChart = new AreaChart();
            _upChart.Configure("Upload rate", "B/s", Color.FromArgb(0xC0, 0x5A, 0x3A), 120, FmtRate);
            _upChart.Location = new Point(8, 148);
            _upChart.Size = new Size(440, 130);
            _upChart.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            var stats = new GroupBox();
            stats.Text = "Summary";
            stats.ForeColor = AppTheme.GroupText;
            stats.Location = new Point(8, 290);
            stats.Size = new Size(420, 132);

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 2;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.Padding = new Padding(6, 4, 6, 6);

            _lblDown = AddStat(grid, "Download");
            _lblUp = AddStat(grid, "Upload");
            _lblTotal = AddStat(grid, "Total transferred");
            _lblDuration = AddStat(grid, "Session duration");
            _lblLatency = AddStat(grid, "Tunnel latency");
            stats.Controls.Add(grid);

            Controls.Add(_downChart);
            Controls.Add(_upChart);
            Controls.Add(stats);

            // Keep equal left/right gaps from the panel edge for the charts.
            LayoutCharts();
            SetActiveVisuals(false);
        }

        // The charts are anchored Top|Left|Right so they track width changes,
        // but we also set their initial width here so the right gap to the
        // window matches the left gap (both = ChartMargin) instead of relying
        // on whatever the design-time width happened to be.
        private const int ChartMargin = 8;
        private void LayoutCharts()
        {
            int w = ClientSize.Width - ChartMargin * 2;
            if (w < 60) w = 60;
            _downChart.Left = ChartMargin;
            _upChart.Left = ChartMargin;
            _downChart.Width = w;
            _upChart.Width = w;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutCharts();
        }

        private Label AddStat(TableLayoutPanel t, string label)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

            var lbl = new Label();
            lbl.Text = label + ":";
            lbl.Font = new Font("Segoe UI", 9F);
            lbl.ForeColor = AppTheme.FieldLabel;
            lbl.TextAlign = ContentAlignment.MiddleRight;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(3, 1, 6, 1);

            var val = new Label();
            val.Text = "\u2014";
            val.Font = new Font("Segoe UI", 9F);
            val.ForeColor = AppTheme.FieldValue;
            val.TextAlign = ContentAlignment.MiddleLeft;
            val.Dock = DockStyle.Fill;
            val.Margin = new Padding(0, 1, 3, 1);

            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(val, 1, row);
            return val;
        }

        private void SetActiveVisuals(bool active)
        {
            // Charts are always visible (flat at zero when idle, like LuCI). The
            // idle hint only shows when there's no active session.
            _downChart.Visible = true;
            _upChart.Visible = true;
        }

        /// <summary>Reset chart history and counters when a session starts/stops.</summary>
        public void SessionReset(bool active)
        {
            _prevRx = _prevTx = -1;
            _prevTime = DateTime.MinValue;
            _sessionStart = active ? DateTime.Now : DateTime.MinValue;
            _downChart.Reset();
            _upChart.Reset();
            SetActiveVisuals(active);
            if (!active)
            {
                _lblDown.Text = _lblUp.Text = _lblTotal.Text = "\u2014";
                _lblDuration.Text = _lblLatency.Text = "\u2014";
            }
        }

        /// <summary>Feed a fresh status snapshot (called ~1/second).</summary>
        public void UpdateStats(TunnelStatus s)
        {
            SetActiveVisuals(true);
            DateTime now = DateTime.Now;

            double downRate = 0, upRate = 0;
            if (_prevRx >= 0 && _prevTime != DateTime.MinValue)
            {
                double dt = (now - _prevTime).TotalSeconds;
                if (dt > 0.1)
                {
                    downRate = Math.Max(0, (s.RxBytes - _prevRx) / dt);
                    upRate = Math.Max(0, (s.TxBytes - _prevTx) / dt);
                }
            }
            _prevRx = s.RxBytes; _prevTx = s.TxBytes; _prevTime = now;

            _downChart.Push(downRate);
            _upChart.Push(upRate);

            _lblDown.Text = FmtRate(downRate);
            _lblUp.Text = FmtRate(upRate);
            _lblTotal.Text = FmtBytes(s.RxBytes) + " down / " + FmtBytes(s.TxBytes) + " up";

            if (_sessionStart != DateTime.MinValue)
            {
                TimeSpan dur = now - _sessionStart;
                _lblDuration.Text = FmtDuration(dur);
            }

            _lblLatency.Text = s.LatencyMs >= 0 ? s.LatencyMs + " ms" : "\u2014";
        }

        // ---- formatting helpers ----
        private static string FmtRate(double bytesPerSec)
        {
            return FmtBytes((long)bytesPerSec) + "/s";
        }
        private static string FmtBytes(long bytes)
        {
            string[] u = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.0")) + " " + u[i];
        }
        private static string FmtDuration(TimeSpan ts)
        {
            long h = (long)ts.TotalHours;
            return h.ToString("00") + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00");
        }
    }
}

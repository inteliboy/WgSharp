using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    // Native CheckBox always paints its 13x13 check GLYPH (not the text) via
    // UxTheme regardless of FlatStyle/BackColor - confirmed in
    // LenovoRepoBuilder (see that project's CLAUDE.md "Visual overhaul"
    // section) that neither FlatStyle.Flat nor a window-theme hint changes
    // it at all. Ported as-is: a fully owner-drawn replacement instead.
    internal class ThemedCheckBox : CheckBox
    {
        const int BoxSize = 14;

        public ThemedCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                      ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            // Clear(), not FillRectangle(), for the background: FillRectangle
            // under SmoothingMode.AntiAlias anti-aliases the rectangle's own
            // edge pixels (partial coverage blended against the double-buffer's
            // black backing), leaving a faint dark seam along the control's
            // bottom edge - stacked rows of these produced a visible hairline
            // grid, making the options list look like a table. Clear() fills
            // every pixel at full coverage regardless of smoothing mode.
            Color parentBg = Parent != null ? Parent.BackColor : AppTheme.WindowBg;
            g.Clear(parentBg);

            g.SmoothingMode = SmoothingMode.AntiAlias;

            int boxTop = (Height - BoxSize) / 2;
            Rectangle box = new Rectangle(1, boxTop, BoxSize, BoxSize);

            Color fill = Checked ? AppTheme.Accent : AppTheme.EntryBg;
            Color border = Checked ? AppTheme.Accent : AppTheme.Border;
            using (SolidBrush b = new SolidBrush(fill)) g.FillRectangle(b, box);
            using (Pen p = new Pen(border)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);

            if (Checked)
            {
                using (Pen check = new Pen(AppTheme.AccentFg, 2f))
                {
                    check.StartCap = LineCap.Round;
                    check.EndCap = LineCap.Round;
                    g.DrawLine(check, box.X + 3, box.Y + 7, box.X + 6, box.Y + 10);
                    g.DrawLine(check, box.X + 6, box.Y + 10, box.X + 11, box.Y + 3);
                }
            }

            Rectangle textRect = new Rectangle(BoxSize + 8, 0, Math.Max(0, Width - BoxSize - 8), Height);
            Color fg = Enabled ? AppTheme.FieldValue : AppTheme.FieldLabel;
            TextRenderer.DrawText(g, Text, Font, textRect, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (Focused)
            {
                Rectangle focusRect = new Rectangle(BoxSize + 6, 2, Math.Max(0, Width - BoxSize - 10), Height - 4);
                ControlPaint.DrawFocusRectangle(g, focusRect);
            }
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    }

    // Replaces GroupBox, whose border+caption chrome always paints in the
    // native (light) visual style regardless of BackColor/ForeColor - no
    // exposed property changes it, and LenovoRepoBuilder has no GroupBox
    // usage to port a fix from. Rather than reproduce GroupBox's boxed
    // border (a "gap cut into the top edge for the label" recipe with no
    // theme precedent in this codebase to verify against live), this draws
    // a plain caption + underline, which groups content just as clearly and
    // is far less error-prone to get right without a native reference.
    //
    // Supports both usage styles in this codebase: Dock=Top + AutoSize +
    // Padding (MainForm's Interface/Peer sections) and fixed Location/Size
    // (StatsPanel's Summary box) - both go through the same
    // DisplayRectangle override, which is the layout engine's own extension
    // point for exactly this ("reserve extra space beyond Padding") purpose.
    internal sealed class ThemedGroupBox : Panel
    {
        public ThemedGroupBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                      ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        }

        // DisplayRectangle is queried on every layout pass and paint; the
        // measured height only depends on the font (and on whether there is
        // a caption at all, which only affects the sample string, not the
        // line height), so measure once per font instead of every call.
        private Font _measuredFont;
        private int _labelAreaHeight;

        int LabelAreaHeight
        {
            get
            {
                Font f = Font;
                if (!ReferenceEquals(f, _measuredFont) || _labelAreaHeight == 0)
                {
                    _labelAreaHeight = TextRenderer.MeasureText("Ag", f).Height + 6;
                    _measuredFont = f;
                }
                return _labelAreaHeight;
            }
        }

        public override Rectangle DisplayRectangle
        {
            get
            {
                Rectangle r = base.DisplayRectangle;
                int extra = LabelAreaHeight;
                return new Rectangle(r.X, r.Y + extra, r.Width, Math.Max(0, r.Height - extra));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color bg = Parent != null ? Parent.BackColor : AppTheme.WindowBg;
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, ClientRectangle);

            if (!string.IsNullOrEmpty(Text))
                TextRenderer.DrawText(g, Text, Font, new Point(0, 0), AppTheme.GroupText);

            int lineY = LabelAreaHeight - 3;
            using (Pen p = new Pen(AppTheme.Border)) g.DrawLine(p, 0, lineY, Math.Max(0, Width - 1), lineY);
        }
    }
}

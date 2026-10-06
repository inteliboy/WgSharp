using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Builds the "label: value" field rows used inside the Interface and Peer
    /// group boxes, matching the official app: right-aligned grey labels in a
    /// fixed column, selectable values beside them.
    /// </summary>
    internal static class FieldGrid
    {
        public static readonly Font LabelFont = new Font("Segoe UI", 9F);
        public static readonly Font ValueFont = new Font("Segoe UI", 9F);
        public static readonly Font KeyFont = new Font("Consolas", 9F);

        /// <summary>Add a row whose value is an arbitrary control (e.g. a StatusRow).</summary>
        public static void AddCustomRow(TableLayoutPanel t, string label, Control valueControl)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));

            var lbl = new Label();
            lbl.Text = label + ":";
            lbl.Font = LabelFont;
            lbl.ForeColor = AppTheme.FieldLabel;
            lbl.TextAlign = ContentAlignment.MiddleRight;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(3, 2, 6, 2);

            valueControl.ForeColor = AppTheme.FieldValue;
            valueControl.Dock = DockStyle.Fill;
            valueControl.Margin = new Padding(0, 2, 3, 2);

            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(valueControl, 1, row);
        }

        public static TableLayoutPanel Create()
        {
            var t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.ColumnCount = 2;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.Padding = new Padding(6, 4, 6, 8);
            t.BackColor = Color.Transparent;
            return t;
        }

        /// <summary>Add a row; returns the value label so the caller can update it later.</summary>
        public static Label AddRow(TableLayoutPanel t, string label, string value)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var lbl = new Label();
            lbl.Text = label + ":";
            lbl.Font = LabelFont;
            lbl.ForeColor = AppTheme.FieldLabel;
            lbl.TextAlign = ContentAlignment.TopRight;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(3, 4, 6, 2);
            lbl.AutoSize = true;
            lbl.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            var val = new Label();
            val.Text = value;
            val.Font = ValueFont;
            val.ForeColor = AppTheme.FieldValue;
            val.TextAlign = ContentAlignment.TopLeft;
            val.Dock = DockStyle.Fill;
            val.Margin = new Padding(0, 4, 3, 2);
            val.AutoSize = false;
            val.UseMnemonic = false;
            WrapToRow(t, row, val, 21);

            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(val, 1, row);
            return val;
        }

        /// <summary>
        /// Like AddRow but for base64 keys: a monospace label that
        /// that wraps if the pane is too narrow for a 44-char key.
        /// </summary>
        public static Label AddKeyRow(TableLayoutPanel t, string label, string value)
        {
            int row = t.RowCount;
            t.RowCount = row + 1;
            t.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));

            var lbl = new Label();
            lbl.Text = label + ":";
            lbl.Font = LabelFont;
            lbl.ForeColor = AppTheme.FieldLabel;
            lbl.TextAlign = ContentAlignment.TopRight;
            lbl.Dock = DockStyle.Fill;
            lbl.Margin = new Padding(3, 4, 6, 2);

            var val = new Label();
            val.Text = value;
            val.Font = KeyFont;  // monospace, like the official key display
            val.ForeColor = AppTheme.FieldValue;
            val.TextAlign = ContentAlignment.TopLeft;
            val.Dock = DockStyle.Fill;
            val.Margin = new Padding(0, 4, 3, 2);
            val.AutoSize = false;
            val.UseMnemonic = false;
            // One line when the pane is wide enough for the 44-char key; wraps onto a second
            // line (rather than ellipsizing the end of a key off-screen) when it isn't.
            WrapToRow(t, row, val, 21);

            t.Controls.Add(lbl, 0, row);
            t.Controls.Add(val, 1, row);
            return val;
        }

        /// <summary>
        /// Keeps a fixed-size value label fully visible: the row grows to however many lines
        /// the text needs at the label's current width (re-measured when the width or the
        /// text changes), never below minHeight. Unbreakable text such as a base64 key is
        /// broken at character level by WordBreak, so it wraps instead of being clipped.
        /// </summary>
        private static void WrapToRow(TableLayoutPanel t, int row, Label val, int minHeight)
        {
            EventHandler fit = delegate
            {
                if (val.Width <= 0 || row >= t.RowStyles.Count) return;
                Size sz = TextRenderer.MeasureText(val.Text, val.Font, new Size(val.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.TextBoxControl);
                int want = Math.Max(minHeight, sz.Height + val.Margin.Vertical + 2);
                RowStyle rs = t.RowStyles[row];
                if (rs.SizeType != SizeType.Absolute || (int)rs.Height != want)
                {
                    rs.SizeType = SizeType.Absolute;
                    rs.Height = want;
                }
            };
            val.SizeChanged += fit;
            val.TextChanged += fit;
        }

        /// <summary>Wrap a long base64 key the way the official app does (it just wraps).</summary>
        public static string FormatKey(byte[] key)
        {
            if (key == null) return "(none)";
            return Convert.ToBase64String(key);
        }
    }
}

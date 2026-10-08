using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    // Drop-in MessageBox.Show replacement themed to match the rest of the
    // app - native MessageBox always paints itself in the current Windows
    // visual style regardless of any app setting, so it ignores this app's
    // theme entirely. Ported as-is from LenovoRepoBuilder's Dialogs.cs. The
    // default button is accent blue, "No" is red, "Cancel" is orange, every
    // other button stays neutral.
    internal static class ThemedMessageBox
    {
        public static DialogResult Show(IWin32Window owner, string text, string caption,
            MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            using (Form f = new Form())
            {
                f.Text = caption;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.BackColor = AppTheme.WindowBg;
                f.Font = new Font("Segoe UI", 9.75f);
                f.AutoScaleMode = AutoScaleMode.None;
                f.HandleCreated += delegate
                {
                    NativeMethods.SetDarkTitleBar(f.Handle, AppTheme.IsDark);
                    NativeMethods.SetBorderAndCaptionColor(f.Handle,
                        AppTheme.IsDark ? AppTheme.Border : Color.Empty,
                        AppTheme.IsDark ? AppTheme.Surface : Color.Empty);
                };

                Icon sysIcon = IconFor(icon);
                int picBottom = 0;
                if (sysIcon != null)
                {
                    PictureBox pic = new PictureBox();
                    pic.Image = sysIcon.ToBitmap();
                    pic.SizeMode = PictureBoxSizeMode.AutoSize;
                    pic.Location = Dpi.Pt(20, 20);
                    f.Controls.Add(pic);
                    picBottom = pic.Bottom;
                }
                int textLeft = sysIcon != null ? Dpi.S(66) : Dpi.S(20);

                Label lbl = new Label();
                lbl.Text = text;
                lbl.Font = f.Font;
                lbl.ForeColor = AppTheme.FieldValue;
                lbl.BackColor = Color.Transparent;
                lbl.AutoSize = true;
                lbl.MaximumSize = new Size(Dpi.S(360), 0);
                lbl.Location = new Point(textLeft, Dpi.S(22));
                f.Controls.Add(lbl);

                int contentBottom = Math.Max(lbl.Bottom, picBottom) + Dpi.S(24);
                int clientWidth = Math.Max(Dpi.S(360), textLeft + lbl.Width + Dpi.S(20));

                string[] labels; DialogResult[] results;
                ButtonSpecsFor(buttons, out labels, out results);

                int btnH = Dpi.S(30), gap = Dpi.S(10), btnBottomMargin = Dpi.S(16);
                Font btnFont = f.Font;

                int uniformBtnW = 0;
                for (int i = 0; i < labels.Length; i++)
                    uniformBtnW = Math.Max(uniformBtnW, TextRenderer.MeasureText(labels[i], btnFont).Width + Dpi.S(30));

                Button[] btns = new Button[labels.Length];
                for (int i = 0; i < labels.Length; i++)
                {
                    Button b = new Button();
                    b.Text = labels[i];
                    b.Font = btnFont;
                    b.FlatStyle = FlatStyle.Flat;
                    b.UseVisualStyleBackColor = false;
                    b.Height = btnH;
                    b.Width = uniformBtnW;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = AppTheme.Border;
                    b.Cursor = Cursors.Hand;

                    bool isDefault = i == 0;
                    bool isNo = results[i] == DialogResult.No;
                    bool isCancel = results[i] == DialogResult.Cancel;

                    if (isNo) { b.BackColor = AppTheme.Err; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; }
                    else if (isCancel) { b.BackColor = AppTheme.Warn; b.ForeColor = Color.White; b.FlatAppearance.BorderSize = 0; }
                    else if (isDefault) { b.BackColor = AppTheme.Accent; b.ForeColor = AppTheme.AccentFg; b.FlatAppearance.BorderSize = 0; }
                    else { b.BackColor = AppTheme.Surface; b.ForeColor = AppTheme.FieldValue; }

                    DialogResult res = results[i];
                    b.Click += delegate(object s, EventArgs e) { f.Tag = res; f.DialogResult = res; f.Close(); };

                    btns[i] = b;
                }
                int totalBtnW = (uniformBtnW + gap) * labels.Length - gap;

                clientWidth = Math.Max(clientWidth, totalBtnW + Dpi.S(40));
                int x = (clientWidth - totalBtnW) / 2;
                foreach (Button b in btns)
                {
                    b.Location = new Point(x, contentBottom);
                    f.Controls.Add(b);
                    x += b.Width + gap;
                }

                f.ClientSize = new Size(clientWidth, contentBottom + btnH + btnBottomMargin);
                f.AcceptButton = btns[0];
                Button cancelBtn = null;
                for (int i = 0; i < results.Length; i++) if (results[i] == DialogResult.Cancel) cancelBtn = btns[i];
                f.CancelButton = cancelBtn != null ? cancelBtn : btns[btns.Length - 1];

                DialogResult dr = owner != null ? f.ShowDialog(owner) : f.ShowDialog();
                return dr;
            }
        }

        static Icon IconFor(MessageBoxIcon icon)
        {
            switch (icon)
            {
                case MessageBoxIcon.Error: return SystemIcons.Error;
                case MessageBoxIcon.Warning: return SystemIcons.Warning;
                case MessageBoxIcon.Information: return SystemIcons.Information;
                case MessageBoxIcon.Question: return SystemIcons.Question;
                default: return null;
            }
        }

        static void ButtonSpecsFor(MessageBoxButtons buttons, out string[] labels, out DialogResult[] results)
        {
            switch (buttons)
            {
                case MessageBoxButtons.OKCancel:
                    labels = new string[] { "OK", "Cancel" };
                    results = new DialogResult[] { DialogResult.OK, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.YesNo:
                    labels = new string[] { "Yes", "No" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No };
                    break;
                case MessageBoxButtons.YesNoCancel:
                    labels = new string[] { "Yes", "No", "Cancel" };
                    results = new DialogResult[] { DialogResult.Yes, DialogResult.No, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.RetryCancel:
                    labels = new string[] { "Retry", "Cancel" };
                    results = new DialogResult[] { DialogResult.Retry, DialogResult.Cancel };
                    break;
                case MessageBoxButtons.AbortRetryIgnore:
                    labels = new string[] { "Abort", "Retry", "Ignore" };
                    results = new DialogResult[] { DialogResult.Abort, DialogResult.Retry, DialogResult.Ignore };
                    break;
                default:
                    labels = new string[] { "OK" };
                    results = new DialogResult[] { DialogResult.OK };
                    break;
            }
        }
    }
}

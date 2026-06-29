using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// A small modal single-line text prompt — used where a value is needed
    /// that doesn't come from a file (so there's no filename to default a
    /// name to), such as naming a tunnel scanned from a QR code.
    /// </summary>
    public sealed class TextInputDialog : Form
    {
        private readonly TextBox _value;
        public string Value { get { return _value.Text; } }

        public TextInputDialog(string title, string prompt, string defaultValue)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(380, 130);

            var lbl = new Label
            {
                Text = prompt,
                Location = new Point(16, 14),
                Size = new Size(348, 36),
                AutoSize = false
            };

            _value = new TextBox
            {
                Location = new Point(16, 54),
                Size = new Size(348, 24),
                Text = defaultValue ?? ""
            };

            var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Size = new Size(84, 28), Location = new Point(196, 90) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 28), Location = new Point(284, 90) };

            ok.Click += delegate
            {
                if (_value.Text.Trim().Length == 0)
                {
                    MessageBox.Show(this, "Please enter a name.", "WgSharp",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.Add(lbl);
            Controls.Add(_value);
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;

            Load += delegate { _value.SelectAll(); _value.Focus(); };
        }

        /// <summary>Prompts for a single line of text. Returns null if cancelled.</summary>
        public static string Ask(IWin32Window owner, string title, string prompt, string defaultValue)
        {
            using (var d = new TextInputDialog(title, prompt, defaultValue))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Value : null;
        }
    }
}

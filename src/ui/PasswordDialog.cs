using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// A small modal password prompt. In "confirm" mode it shows two fields and
    /// requires them to match (used when setting a password on create/import). In
    /// single mode it shows one field (used when unlocking to activate/decrypt).
    /// </summary>
    public sealed class PasswordDialog : Form
    {
        private readonly TextBox _pw;
        private readonly TextBox _confirm;
        public string Password { get { return _pw.Text; } }

        public PasswordDialog(string title, string prompt, bool confirm)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(380, confirm ? 168 : 130);

            var lbl = new Label
            {
                Text = prompt,
                Location = new Point(16, 14),
                Size = new Size(348, 36),
                AutoSize = false
            };

            var lblPw = new Label { Text = "Password:", Location = new Point(16, 56), Size = new Size(90, 22), TextAlign = ContentAlignment.MiddleLeft };
            _pw = new TextBox { Location = new Point(110, 54), Size = new Size(254, 24), UseSystemPasswordChar = true };

            _confirm = new TextBox { Location = new Point(110, 84), Size = new Size(254, 24), UseSystemPasswordChar = true };
            var lblConfirm = new Label { Text = "Confirm:", Location = new Point(16, 86), Size = new Size(90, 22), TextAlign = ContentAlignment.MiddleLeft };

            int btnY = confirm ? 124 : 90;
            var ok = new Button { Text = "OK", DialogResult = DialogResult.None, Size = new Size(84, 28), Location = new Point(196, btnY) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(84, 28), Location = new Point(284, btnY) };

            ok.Click += delegate
            {
                if (_pw.Text.Length == 0)
                {
                    MessageBox.Show(this, "Please enter a password.", "WgSharp",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (confirm && _pw.Text != _confirm.Text)
                {
                    MessageBox.Show(this, "The passwords do not match.", "WgSharp",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            Controls.Add(lbl);
            Controls.Add(lblPw);
            Controls.Add(_pw);
            if (confirm)
            {
                Controls.Add(lblConfirm);
                Controls.Add(_confirm);
            }
            Controls.Add(ok);
            Controls.Add(cancel);

            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>Convenience: prompt for a new password (with confirm). Returns null if cancelled.</summary>
        public static string AskNew(IWin32Window owner, string prompt)
        {
            using (var d = new PasswordDialog("Set password", prompt, true))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Password : null;
        }

        /// <summary>Convenience: prompt for an existing password. Returns null if cancelled.</summary>
        public static string AskExisting(IWin32Window owner, string prompt)
        {
            using (var d = new PasswordDialog("Enter password", prompt, false))
                return d.ShowDialog(owner) == DialogResult.OK ? d.Password : null;
        }
    }
}

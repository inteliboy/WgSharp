using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Modal editor for a tunnel's name and raw .conf text, mirroring the
    /// official app's "Create new tunnel" dialog including syntax coloring:
    /// section headers, keys, and values are tinted as you type.
    /// </summary>
    public sealed class EditConfigDialog : Form
    {
        private TextBox _name;
        private RichTextBox _config;
        private Label _pubKeyValue;
        private CheckBox _blockUntunneled;
        private bool _highlighting;
        private Timer _debounce;

        // palette approximating the official editor
        private static readonly Color ColSection = Color.FromArgb(0x00, 0x4A, 0xCC); // [Interface]
        private static readonly Color ColKey = Color.FromArgb(0x7A, 0x3E, 0x9D);     // PrivateKey
        private static readonly Color ColValue = Color.FromArgb(0x1F, 0x5F, 0xBF);   // = values
        private static readonly Color ColComment = Color.FromArgb(0x3C, 0x8A, 0x3C); // # comments
        private static readonly Color ColDefault = Color.FromArgb(0x20, 0x20, 0x20);

        public string TunnelName { get { return _name.Text.Trim(); } }
        public string ConfigText { get { return _config.Text; } }

        public EditConfigDialog(string name, string configText, bool blockUntunneled)
        {
            Text = "Edit tunnel";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(520, 470);
            MinimumSize = new Size(420, 360);
            Font = new Font("Segoe UI", 9F);
            BackColor = Color.White;

            var lblName = new Label
            {
                Text = "Name:",
                Location = new Point(14, 17),
                Size = new Size(80, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = FieldGrid.LabelColor
            };
            _name = new TextBox
            {
                Text = name,
                Location = new Point(98, 14),
                Size = new Size(408, 24),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var lblPub = new Label
            {
                Text = "Public key:",
                Location = new Point(14, 47),
                Size = new Size(80, 20),
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = FieldGrid.LabelColor
            };
            _pubKeyValue = new Label
            {
                Text = "(derived from PrivateKey)",
                Location = new Point(98, 47),
                Size = new Size(408, 20),
                ForeColor = Color.FromArgb(0x55, 0x55, 0x55),
                Font = new Font("Consolas", 8.5F),
                AutoEllipsis = true,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _config = new RichTextBox
            {
                Text = configText,
                Font = new Font("Consolas", 10F),
                Location = new Point(14, 76),
                Size = new Size(492, 312),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
                AcceptsTab = true,
                WordWrap = false,
                HideSelection = false
            };
            _config.TextChanged += new EventHandler(OnConfigChanged);

            _regularFont = new Font(_config.Font, FontStyle.Regular);
            _boldFont = new Font(_config.Font, FontStyle.Bold);

            _debounce = new Timer();
            _debounce.Interval = 250;
            _debounce.Tick += new EventHandler(OnDebounceTick);

            var btnSave = new Button
            {
                Text = "Save",
                DialogResult = DialogResult.OK,
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            var btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Size = new Size(88, 28),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            btnSave.Location = new Point(ClientSize.Width - 2 * 94 - 14, ClientSize.Height - 38);
            btnCancel.Location = new Point(ClientSize.Width - 94 - 8, ClientSize.Height - 38);

            _blockUntunneled = new CheckBox
            {
                Text = "Block untunneled traffic (kill-switch)",
                Location = new Point(14, ClientSize.Height - 34),
                Size = new Size(280, 22),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                ForeColor = Color.FromArgb(0x20, 0x20, 0x20)
            };

            Controls.Add(lblName);
            Controls.Add(_name);
            Controls.Add(lblPub);
            Controls.Add(_pubKeyValue);
            Controls.Add(_config);
            Controls.Add(_blockUntunneled);
            Controls.Add(btnSave);
            Controls.Add(btnCancel);

            AcceptButton = null; // Enter should insert newlines in the editor
            CancelButton = btnCancel;

            _blockUntunneled.Checked = blockUntunneled;
            _blockUntunneled.CheckedChanged += new EventHandler(OnBlockToggled);

            Highlight();
            UpdatePublicKey();
            UpdateKillSwitchVisibility();
        }

        // Toggling the kill-switch rewrites the AllowedIPs line, exactly like the
        // official client. For BOTH address families:
        //   checked   -> the literal default routes 0.0.0.0/0 and ::/0
        //   unchecked -> the /1 split form (0.0.0.0/1, 128.0.0.0/1, ::/1, 8000::/1),
        //                which covers everything without triggering the firewall block.
        private void OnBlockToggled(object sender, EventArgs e)
        {
            string v4full = "0.0.0.0/0";
            string v4a = "0.0.0.0/1", v4b = "128.0.0.0/1";
            string v6full = "::/0";
            string v6a = "::/1", v6b = "8000::/1";

            string[] lines = _config.Text.Replace("\r\n", "\n").Split('\n');
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim().ToLowerInvariant() != "allowedips") continue;

                string value = line.Substring(eq + 1);
                string normalized = value.Replace(" ", "");

                if (_blockUntunneled.Checked)
                {
                    // --- v4: split -> full (or insert full if absent) ---
                    if (normalized.Contains(v4a) && normalized.Contains(v4b))
                    {
                        value = ReplacePair(value, v4a, v4b, v4full); changed = true;
                    }
                    else if (!Contains(value, v4full))
                    {
                        value = AppendToken(value, v4full); changed = true;
                    }
                    normalized = value.Replace(" ", "");

                    // --- v6: split -> full, or insert ::/0 if no v6 default present ---
                    if (normalized.Contains(v6a) && normalized.Contains(v6b))
                    {
                        value = ReplacePair(value, v6a, v6b, v6full); changed = true;
                    }
                    else if (!Contains(value, v6full))
                    {
                        value = AppendToken(value, v6full); changed = true;
                    }
                }
                else
                {
                    // --- v4: full -> split ---
                    if (Contains(value, v4full))
                    {
                        value = ReplaceToken(value, v4full, v4a + ", " + v4b); changed = true;
                    }
                    // --- v6: full -> split (only if a v6 default is present) ---
                    if (Contains(value, v6full))
                    {
                        value = ReplaceToken(value, v6full, v6a + ", " + v6b); changed = true;
                    }
                }
                lines[i] = line.Substring(0, eq + 1) + value;
            }

            if (changed)
            {
                _highlighting = true;
                _config.Text = string.Join("\r\n", lines);
                _highlighting = false;
                Highlight();
            }
        }

        // True if value contains the exact CIDR token (tolerating surrounding spaces).
        private static bool Contains(string value, string token)
        {
            foreach (string part in value.Split(','))
                if (part.Trim() == token) return true;
            return false;
        }

        // Append a token to the end of the comma list, preserving existing entries.
        private static string AppendToken(string value, string token)
        {
            string t = value.Trim();
            if (t.Length == 0) return " " + token;
            return " " + t + ", " + token;
        }

        private static string ReplaceToken(string value, string token, string replacement)
        {
            // replace the token while tolerating surrounding spaces
            string[] parts = value.Split(',');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Trim() == token) parts[i] = " " + replacement;
            return string.Join(",", parts);
        }

        private static string ReplacePair(string value, string a, string b, string replacement)
        {
            // remove a and b, then put replacement where a was
            var list = new System.Collections.Generic.List<string>();
            bool placed = false;
            foreach (string part in value.Split(','))
            {
                string t = part.Trim();
                if (t == a) { if (!placed) { list.Add(replacement); placed = true; } }
                else if (t == b) { /* drop */ }
                else if (t.Length > 0) list.Add(t);
            }
            return " " + string.Join(", ", list.ToArray());
        }

        private void OnConfigChanged(object sender, EventArgs e)
        {
            if (_highlighting) return;
            // Debounce: restart the timer; highlight once typing pauses.
            _debounce.Stop();
            _debounce.Start();
        }

        private void OnDebounceTick(object sender, EventArgs e)
        {
            _debounce.Stop();
            Highlight();
            UpdatePublicKey();
            UpdateKillSwitchVisibility();
        }

        // The "Block untunneled traffic" checkbox only appears when the config
        // routes all traffic (AllowedIPs contains 0.0.0.0/0 or ::/0, in either the
        // literal or the /1-split form), exactly like the official client.
        private void UpdateKillSwitchVisibility()
        {
            bool routesAll = false;
            foreach (string raw in _config.Text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim().ToLowerInvariant() != "allowedips") continue;
                string v = line.Substring(eq + 1).Replace(" ", "");
                if (v.Contains("0.0.0.0/0") || v.Contains("::/0")) { routesAll = true; break; }
                if (v.Contains("0.0.0.0/1") && v.Contains("128.0.0.0/1")) { routesAll = true; break; }
            }
            _blockUntunneled.Visible = routesAll;
        }

        private void UpdatePublicKey()
        {
            // Derive the public key from the PrivateKey line alone, independent of
            // whether the rest of the config is complete/valid yet. This matches the
            // official client, which shows the public key as soon as a private key
            // exists (e.g. right after generating an empty tunnel).
            byte[] priv = ExtractPrivateKey(_config.Text);
            if (priv != null)
            {
                try
                {
                    byte[] pub = WgSharp.Crypto.Curve25519.ScalarMultBase(priv);
                    _pubKeyValue.Text = Convert.ToBase64String(pub);
                    _pubKeyValue.ForeColor = Color.FromArgb(0x20, 0x20, 0x20);
                    return;
                }
                catch { }
            }
            _pubKeyValue.Text = "(set a valid PrivateKey)";
            _pubKeyValue.ForeColor = Color.FromArgb(0x99, 0x99, 0x99);
        }

        // Pull the PrivateKey value from the [Interface] section and decode it,
        // returning a 32-byte key or null if absent/invalid.
        private static byte[] ExtractPrivateKey(string text)
        {
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                if (line.Substring(0, eq).Trim().ToLowerInvariant() != "privatekey") continue;
                string val = line.Substring(eq + 1).Trim();
                if (val.Length == 0) return null;
                try
                {
                    byte[] k = Convert.FromBase64String(val);
                    return k.Length == 32 ? k : null;
                }
                catch { return null; }
            }
            return null;
        }

        // Re-color the whole document. For a small config this is cheap; we guard
        // reentrancy and preserve the caret/scroll position.
        private void Highlight()
        {
            _highlighting = true;

            int selStart = _config.SelectionStart;
            int selLen = _config.SelectionLength;
            _config.SuspendLayout();

            int lineStart = 0;
            string[] lines = _config.Text.Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.TrimEnd('\r');
                ColorLine(lineStart, line);
                lineStart += raw.Length + 1; // +1 for the '\n' removed by Split
            }

            _config.SelectionStart = selStart;
            _config.SelectionLength = selLen;
            _config.SelectionColor = ColDefault;
            _config.ResumeLayout();
            _highlighting = false;
        }

        private void ColorLine(int offset, string line)
        {
            string trimmed = line.TrimStart();
            int lead = line.Length - trimmed.Length;

            if (trimmed.StartsWith("#") || trimmed.StartsWith(";"))
            {
                Apply(offset, line.Length, ColComment, false);
                return;
            }
            if (trimmed.StartsWith("[") )
            {
                Apply(offset, line.Length, ColSection, true);
                return;
            }

            int eq = line.IndexOf('=');
            if (eq < 0)
            {
                Apply(offset, line.Length, ColDefault, false);
                return;
            }

            // key (up to '='), the '=' and value
            Apply(offset, eq, ColKey, true);
            Apply(offset + eq, 1, ColDefault, false);             // the '='
            Apply(offset + eq + 1, line.Length - eq - 1, ColValue, false);
        }

        private void Apply(int start, int length, Color color, bool bold)
        {
            if (length <= 0) return;
            if (start < 0) start = 0;
            if (start + length > _config.TextLength) length = _config.TextLength - start;
            if (length <= 0) return;
            _config.SelectionStart = start;
            _config.SelectionLength = length;
            _config.SelectionColor = color;
            _config.SelectionFont = bold ? _boldFont : _regularFont;
        }

        private Font _regularFont;
        private Font _boldFont;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_debounce != null) _debounce.Dispose();
                if (_regularFont != null) _regularFont.Dispose();
                if (_boldFont != null) _boldFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

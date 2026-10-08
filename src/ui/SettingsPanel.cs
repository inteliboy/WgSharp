using System;
using System.Drawing;
using System.Windows.Forms;
using WgSharp.Core;

namespace WgSharp.Ui
{
    /// <summary>
    /// The Settings tab: portable mode (password-encrypted configs in a folder
    /// next to the exe) and the WireGuardNT backend toggle. Changes are
    /// persisted immediately. PortableModeChanged lets the form reload the
    /// tunnel list when the store location changes.
    /// </summary>
    public sealed class SettingsPanel : Panel
    {
        private readonly CheckBox _portable;
        private readonly CheckBox _wgNt;
        private readonly CheckBox _autoStart;
        private readonly CheckBox _guiAutoStart;
        private readonly CheckBox _debugLog;
        private readonly CheckBox _checkUpdates;
        private readonly CheckBox _darkTheme;
        private readonly CheckBox _autoConnect;
        private readonly CheckBox _wiredTrusted;
        private readonly Label _lblAutoTunnel;
        private readonly Label _lblTrusted;
        private readonly TextBox _txtTrusted;
        private readonly Panel _trustedBorder;
        // Descriptions now appear as hover tooltips (2-second delay) instead of
        // always-visible labels, leaving more room for the options themselves.
        private readonly ToolTip _tips;
        private bool _loading;

        public event Action PortableModeChanged;
        public event Action ThemeToggled;
        public event Action AutoConnectChanged;

        public SettingsPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.PanelBg;
            Padding = new Padding(10);
            AutoScroll = true;   // never clip the last item when the window is short

            // Order: portable, WireGuardNT, background service, GUI at login,
            // check for updates, debug log, dark theme.
            _tips = new ToolTip
            {
                AutoPopDelay = 20000,  // keep the tip visible long enough to read
                InitialDelay = 500,    // ~0.5s of hover before showing
                ReshowDelay = 300,
                ShowAlways = true
            };

            _portable = MakeOption("Portable mode",
                "Stores tunnel configs in a \"conf\" folder next to the app, password-encrypted " +
                "instead of using Windows DPAPI, so they can travel with the app folder to another " +
                "machine.");
            _portable.CheckedChanged += OnPortableChanged;

            _wgNt = MakeOption("Use WireGuardNT (kernel) backend",
                "Uses the official kernel WireGuard driver for higher throughput, instead of the " +
                "built-in managed implementation. Recommended; on by default. Takes effect on the " +
                "next connect.");
            _wgNt.CheckedChanged += OnWgNtChanged;

            _autoStart = MakeOption("Start with Windows (background service)",
                "Installs a background service that reconnects your last tunnel automatically, even " +
                "before you log in. Only works with the normal (non-portable) store, since the " +
                "service has no one around to type a password for a portable tunnel.");
            _autoStart.CheckedChanged += OnAutoStartChanged;

            _guiAutoStart = MakeOption("Start GUI at login (in the tray)",
                "Launches the WgSharp window minimized to the notification area when you log in, like " +
                "the official client. Independent of the background service above: that reconnects " +
                "your tunnel before login; this just puts the tray icon there for you.");
            _guiAutoStart.CheckedChanged += OnGuiAutoStartChanged;

            _checkUpdates = MakeOption("Check for updates",
                "When on (default), WgSharp quietly checks GitHub at startup for a newer release and " +
                "shows a tray notification if one exists. It never downloads or installs anything on " +
                "its own \u2014 clicking the notification just opens the releases page in your browser.");
            _checkUpdates.CheckedChanged += OnCheckUpdatesChanged;

            _debugLog = MakeOption("Debug log",
                "When on, the Log tab shows full diagnostic detail and the background service writes a " +
                "service.log file. When off (default) only the most meaningful messages are shown and " +
                "no service.log is written, to avoid constant disk writes.");
            _debugLog.CheckedChanged += OnDebugLogChanged;

            _darkTheme = MakeOption("Dark theme",
                "Switches the app between light and dark. Off by default follows Windows' own light/" +
                "dark setting until you toggle this explicitly, after which your choice is remembered.");
            _darkTheme.CheckedChanged += OnDarkThemeChanged;

            _autoConnect = MakeOption("Auto-connect on untrusted networks",
                "Connects the chosen tunnel whenever you join a network that isn't trusted, and " +
                "disconnects it again on a trusted one. Choose the tunnel by right-clicking it in the " +
                "Tunnels tab. Needs the WgSharp window (or tray icon) to be running, and is not " +
                "available in portable mode (a portable tunnel needs a password). Disconnecting " +
                "manually pauses the rule until you change networks.");
            _autoConnect.CheckedChanged += OnAutoConnectChanged;

            _lblAutoTunnel = MakeSubLabel("");
            _lblTrusted = MakeSubLabel("Trusted Wi-Fi networks (comma-separated names):");

            _txtTrusted = new TextBox { Font = new Font("Segoe UI", 9.5F) };
            Ctrl.ThemeEntry(_txtTrusted);
            _txtTrusted.Leave += OnTrustedTextCommitted;
            _txtTrusted.KeyDown += delegate (object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OnTrustedTextCommitted(s, EventArgs.Empty); }
            };
            _trustedBorder = Ctrl.Bordered(_txtTrusted);
            _trustedBorder.Size = new Size(420, 26);
            _tips.SetToolTip(_txtTrusted, WrapTip("Wi-Fi network names (SSIDs) where you don't want the VPN, " +
                "for example your home network. Matching ignores case."));

            _wiredTrusted = MakeOption("Treat wired Ethernet as trusted",
                "When on, a connected wired Ethernet adapter counts as a trusted network, so the " +
                "tunnel is not auto-connected (and is auto-disconnected) while you're plugged in.");
            _wiredTrusted.CheckedChanged += OnWiredTrustedChanged;

            // Lay the options out top to bottom in the order created above. A
            // single tight row height (no help label between them) is what
            // frees up the space; the description is a hover tooltip instead.
            const int leftPad = 16, top = 14, rowH = 34;
            CheckBox[] options = { _portable, _wgNt, _autoStart, _guiAutoStart, _checkUpdates, _debugLog, _darkTheme };
            for (int i = 0; i < options.Length; i++)
            {
                options[i].Location = new Point(leftPad, top + i * rowH);
                options[i].Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
                Controls.Add(options[i]);
            }

            int y = top + options.Length * rowH;
            _autoConnect.Location = new Point(leftPad, y);
            _lblAutoTunnel.Location = new Point(leftPad + 24, y + 28);
            _lblTrusted.Location = new Point(leftPad + 24, y + 52);
            _trustedBorder.Location = new Point(leftPad + 24, y + 74);
            _wiredTrusted.Location = new Point(leftPad, y + 108);
            foreach (Control c in new Control[] { _autoConnect, _lblAutoTunnel, _lblTrusted, _trustedBorder, _wiredTrusted })
                Controls.Add(c);
        }

        private static Label MakeSubLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                UseMnemonic = false,
                Font = new Font("Segoe UI", 9F),
                ForeColor = AppTheme.FieldLabel,
                BackColor = Color.Transparent
            };
        }

        /// <summary>Shows which tunnel the auto-connect rule will use (set from the tunnel list's context menu).</summary>
        public void UpdateAutoConnectTunnelLabel()
        {
            string t = AppSettings.AutoConnectTunnel;
            _lblAutoTunnel.Text = string.IsNullOrEmpty(t)
                ? "Tunnel: none selected (right-click a tunnel in the Tunnels tab)"
                : "Tunnel: " + t;
        }

        private void OnAutoConnectChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppSettings.AutoConnectEnabled = _autoConnect.Checked;
            AppSettings.Save();
            if (_autoConnect.Checked && string.IsNullOrEmpty(AppSettings.AutoConnectTunnel))
                ThemedMessageBox.Show(this,
                    "Now right-click the tunnel you want to use in the Tunnels tab and choose " +
                    "\"Auto-connect on untrusted networks\".",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            var h = AutoConnectChanged;
            if (h != null) h();
        }

        private void OnWiredTrustedChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppSettings.WiredIsTrusted = _wiredTrusted.Checked;
            AppSettings.Save();
            var h = AutoConnectChanged;
            if (h != null) h();
        }

        private void OnTrustedTextCommitted(object sender, EventArgs e)
        {
            if (_loading) return;
            string v = _txtTrusted.Text.Trim();
            if (v == (AppSettings.TrustedNetworks ?? "")) return;
            AppSettings.TrustedNetworks = v;
            AppSettings.Save();
            var h = AutoConnectChanged;
            if (h != null) h();
        }

        // Builds a settings checkbox with a hover tooltip carrying its
        // description. Width is generous and anchored so the label text isn't
        // clipped; the tooltip (not an inline label) holds the explanation.
        private CheckBox MakeOption(string text, string tip)
        {
            var cb = new ThemedCheckBox
            {
                Text = text,
                Size = new Size(460, 24),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                AutoSize = false
            };
            _tips.SetToolTip(cb, WrapTip(tip));
            return cb;
        }

        // WinForms ToolTip doesn't word-wrap on its own — without explicit
        // newlines it draws one long line that can span the whole screen. This
        // inserts breaks at word boundaries near a target column so the tip
        // stays a readable block.
        private static string WrapTip(string text)
        {
            const int maxChars = 60; // roughly the comfortable width of the tip box
            if (string.IsNullOrEmpty(text)) return text;
            var sb = new System.Text.StringBuilder(text.Length + 16);
            int lineLen = 0;
            foreach (string word in text.Split(' '))
            {
                if (lineLen > 0 && lineLen + 1 + word.Length > maxChars)
                {
                    sb.Append('\n');
                    lineLen = 0;
                }
                else if (lineLen > 0)
                {
                    sb.Append(' ');
                    lineLen++;
                }
                sb.Append(word);
                lineLen += word.Length;
            }
            return sb.ToString();
        }

        // Called from MainForm.ApplyTheme() on every toggle - the checkboxes
        // are ThemedCheckBox and read AppTheme.* live in their own OnPaint
        // (Invalidate(true), already done at the Form level, is all they
        // need), but this panel's own BackColor was snapshotted once at
        // construction and needs re-assigning explicitly.
        public void RefreshTheme()
        {
            BackColor = AppTheme.PanelBg;
            _lblAutoTunnel.ForeColor = AppTheme.FieldLabel;
            _lblTrusted.ForeColor = AppTheme.FieldLabel;
            Ctrl.ThemeEntry(_txtTrusted);
            _trustedBorder.BackColor = AppTheme.Border;
            _loading = true;
            _darkTheme.Checked = AppTheme.IsDark;
            _loading = false;
            Invalidate(true);
        }

        public void LoadFromSettings()
        {
            _loading = true;
            _portable.Checked = AppSettings.PortableMode;
            _wgNt.Checked = AppSettings.UseWireGuardNt;
            // Installed state IS the truth here — there's no separate
            // AppSettings flag, since "is the service registered with SCM"
            // is the actual thing that matters and can't drift out of sync
            // with a stored bool the way a separate setting could.
            try { _autoStart.Checked = ServiceInstaller.IsInstalled(); }
            catch { _autoStart.Checked = false; }
            try { _guiAutoStart.Checked = LoginAutostart.IsEnabled(); }
            catch { _guiAutoStart.Checked = false; }
            _debugLog.Checked = AppSettings.DebugLog;
            _checkUpdates.Checked = AppSettings.CheckForUpdates;
            _darkTheme.Checked = AppTheme.IsDark;
            _autoConnect.Checked = AppSettings.AutoConnectEnabled;
            _wiredTrusted.Checked = AppSettings.WiredIsTrusted;
            _txtTrusted.Text = AppSettings.TrustedNetworks ?? "";
            UpdateAutoConnectTunnelLabel();
            UpdateExclusivityEnabled();
            _loading = false;
        }

        // Portable mode and the two "start automatically" options are mutually
        // exclusive: a portable tunnel is password-encrypted, and neither the
        // boot-time service nor a login-launched GUI has a human present to
        // type that password, so auto-starting one makes no sense. We enforce
        // it both ways here — when portable is on, the startup options are
        // disabled; when either startup option is on, portable is disabled —
        // and still allow turning OFF whatever is currently on.
        private void UpdateExclusivityEnabled()
        {
            bool portable = AppSettings.PortableMode;
            // Auto-connect needs an unattended activation; portable tunnels need a password.
            _autoConnect.Enabled = !portable;
            _wiredTrusted.Enabled = !portable;
            _txtTrusted.Enabled = !portable;

            // Startup options in portable mode: a portable tunnel is
            // password-encrypted and there's no human at boot/login to type
            // that password, so auto-start is meaningless. Portable mode is
            // also permanently on for a standalone copy (locked by location),
            // so unlike before we DON'T leave an "allow unchecking if currently
            // checked" escape hatch — we force both OFF and hard-disable them,
            // and push that back into settings so a config carried over from a
            // non-portable install can't leave a stale checkmark behind.
            if (portable)
            {
                if (_autoStart.Checked) _autoStart.Checked = false;
                if (_guiAutoStart.Checked) _guiAutoStart.Checked = false;
                if (AppSettings.ServiceWasInstalled) { AppSettings.ServiceWasInstalled = false; AppSettings.Save(); }
                if (AppSettings.StartGuiAtLogin) { AppSettings.StartGuiAtLogin = false; AppSettings.Save(); }

                _autoStart.Enabled = false;
                _guiAutoStart.Enabled = false;
            }
            else
            {
                _autoStart.Enabled = true;
                _guiAutoStart.Enabled = true;
            }

            // Portable mode is determined ENTIRELY by where WgSharp runs from,
            // so the checkbox is informational (checked or not) and always
            // disabled — never a free toggle:
            //   * Proper Program Files install  -> portable OFF, locked. The
            //     installed copy uses the machine DPAPI store and the
            //     background service.
            //   * Anywhere else (zip / USB / standalone folder) -> portable ON,
            //     locked. A copy that travels with its own folder must keep its
            //     configs in that folder (password-encrypted), and can't use a
            //     machine-registered service.
            // This mirrors what the app enforces at startup
            // (InstallLocation.EnforcePortableModeRestriction and the portable
            // first-run default), so the UI can't drift from actual behavior.
            bool installedLocation = WgSharp.Core.InstallLocation.IsInstalled();
            _portable.Enabled = false;
            if (installedLocation)
            {
                _portable.Checked = false;
                _tips.SetToolTip(_portable, WrapTip("Not available: WgSharp was installed via the Setup installer to a " +
                    "fixed location, so it uses the machine config store and the background service. " +
                    "Portable mode is only for the standalone copy that travels with its own folder \u2014 " +
                    "use the zip distribution if you need it."));
            }
            else
            {
                _portable.Checked = true;
                _tips.SetToolTip(_portable, WrapTip("Always on for this copy: WgSharp is running from a standalone " +
                    "location (not a Program Files install), so it keeps its tunnels in its own folder, " +
                    "password-encrypted, and runs them in-process. To use the machine store and the " +
                    "background service instead, install WgSharp with the Setup installer."));
            }
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            try
            {
                // Registering/removing a service is an SCM operation, which
                // the asInvoker GUI can't do directly. When elevated (manual
                // "Run as administrator" / UAC off) do it inline as before;
                // otherwise self-elevate for this one action — the same
                // single-prompt pattern as the first-run setup.
                bool elevated = Elevation.IsProcessElevated();
                if (_autoStart.Checked)
                {
                    if (elevated) ServiceInstaller.EnsureInstalledAndRunning();
                    else
                    {
                        string err;
                        if (!Elevation.RunElevatedSetup(out err))
                            throw new Exception(err == "cancelled"
                                ? "the administrator prompt was cancelled"
                                : err);
                    }
                    AppSettings.ServiceWasInstalled = true;
                    AppSettings.Save();
                }
                else
                {
                    if (elevated) ServiceInstaller.Uninstall();
                    else
                    {
                        string err;
                        if (!Elevation.RunElevated("--elevated-uninstall-service", out err))
                            throw new Exception(err == "cancelled"
                                ? "the administrator prompt was cancelled"
                                : err);
                    }
                    AppSettings.ServiceWasInstalled = false;
                    AppSettings.Save();
                }
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, "Couldn't " + (_autoStart.Checked ? "install" : "remove") +
                    " the background service: " + ex.Message, "WgSharp",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _loading = true;
                _autoStart.Checked = !_autoStart.Checked; // revert
                _loading = false;
            }
            UpdateExclusivityEnabled();
        }

        private void OnWgNtChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppSettings.UseWireGuardNt = _wgNt.Checked;
            AppSettings.Save();
        }

        private void OnGuiAutoStartChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            try
            {
                if (_guiAutoStart.Checked) LoginAutostart.Enable();
                else LoginAutostart.Disable();
                AppSettings.StartGuiAtLogin = _guiAutoStart.Checked;
                AppSettings.Save();
            }
            catch (Exception ex)
            {
                ThemedMessageBox.Show(this, "Couldn't " + (_guiAutoStart.Checked ? "enable" : "disable") +
                    " start-at-login: " + ex.Message, "WgSharp",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                _loading = true;
                _guiAutoStart.Checked = !_guiAutoStart.Checked; // revert
                _loading = false;
            }
            UpdateExclusivityEnabled();
        }

        private void OnDebugLogChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppSettings.DebugLog = _debugLog.Checked;
            AppSettings.Save();
        }

        private void OnCheckUpdatesChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppSettings.CheckForUpdates = _checkUpdates.Checked;
            AppSettings.Save();
        }

        private void OnDarkThemeChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            AppTheme.IsDark = _darkTheme.Checked;
            AppSettings.ThemeIsDark = AppTheme.IsDark;
            AppSettings.ThemeOverrideSet = true;
            AppSettings.Save();
            var h = ThemeToggled;
            if (h != null) h();
        }

        private void OnPortableChanged(object sender, EventArgs e)
        {
            if (_loading) return;

            // Warn that the visible tunnel list comes from a different store now.
            string msg = _portable.Checked
                ? "Portable mode will use a \"conf\" folder next to the app, with " +
                  "password-encrypted configs. Tunnels stored in the normal location " +
                  "won't appear until you switch back. Continue?"
                : "Switching off portable mode will use the normal DPAPI store in " +
                  "C:\\ProgramData. Your portable tunnels won't appear until you switch " +
                  "back. Continue?";
            if (ThemedMessageBox.Show(this, msg, "WgSharp \u2014 portable mode",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                _loading = true;
                _portable.Checked = AppSettings.PortableMode; // revert
                _loading = false;
                return;
            }

            AppSettings.PortableMode = _portable.Checked;
            AppSettings.Save();
            UpdateExclusivityEnabled();
            if (PortableModeChanged != null) PortableModeChanged();
        }
    }
}

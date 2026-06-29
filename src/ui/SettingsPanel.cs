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
        private readonly Label _portableHelp;
        private readonly CheckBox _wgNt;
        private readonly CheckBox _autoStart;
        private readonly Label _autoStartHelp;
        private readonly CheckBox _guiAutoStart;
        private readonly Label _guiAutoStartHelp;
        private readonly CheckBox _debugLog;
        private bool _loading;

        public event Action PortableModeChanged;

        public SettingsPanel()
        {
            Dock = DockStyle.Fill;
            BackColor = AppTheme.PanelBg;
            Padding = new Padding(10);
            AutoScroll = true;   // extra options can exceed the window height

            _portable = new CheckBox
            {
                Text = "Portable mode",
                Location = new Point(16, 10),
                Size = new Size(300, 22),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            _portableHelp = new Label
            {
                Text = "Stores tunnel configs in a \"conf\" folder next to the app, password-" +
                       "encrypted instead of using Windows DPAPI, so they can travel with the " +
                       "app folder to another machine.",
                Location = new Point(36, 32),
                Size = new Size(450, 40),
                ForeColor = AppTheme.FieldLabel
            };
            _portable.CheckedChanged += OnPortableChanged;

            _wgNt = new CheckBox
            {
                Text = "Use WireGuardNT (kernel) backend",
                Location = new Point(16, 78),
                Size = new Size(340, 22),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            var wgNtHelp = new Label
            {
                Text = "Uses the official kernel WireGuard driver for higher throughput, " +
                       "instead of the built-in managed implementation. Recommended; on by " +
                       "default. Takes effect on the next connect.",
                Location = new Point(36, 100),
                Size = new Size(450, 40),
                ForeColor = AppTheme.FieldLabel
            };
            _wgNt.CheckedChanged += OnWgNtChanged;

            _autoStart = new CheckBox
            {
                Text = "Start with Windows (background service)",
                Location = new Point(16, 146),
                Size = new Size(380, 22),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            _autoStartHelp = new Label
            {
                Text = "Installs a background service that reconnects your last tunnel " +
                       "automatically, even before you log in. Only works with the normal " +
                       "(non-portable) store, since the service has no one around to type a " +
                       "password for a portable tunnel.",
                Location = new Point(36, 168),
                Size = new Size(450, 50),
                ForeColor = AppTheme.FieldLabel
            };
            _autoStart.CheckedChanged += OnAutoStartChanged;

            _guiAutoStart = new CheckBox
            {
                Text = "Start GUI at login (in the tray)",
                Location = new Point(16, 224),
                Size = new Size(380, 22),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            _guiAutoStartHelp = new Label
            {
                Text = "Launches the WgSharp window minimized to the notification area when " +
                       "you log in, like the official client. Independent of the background " +
                       "service above: that reconnects your tunnel before login; this just " +
                       "puts the tray icon there for you.",
                Location = new Point(36, 246),
                Size = new Size(450, 50),
                ForeColor = AppTheme.FieldLabel
            };
            _guiAutoStart.CheckedChanged += OnGuiAutoStartChanged;

            _debugLog = new CheckBox
            {
                Text = "Debug log",
                Location = new Point(16, 302),
                Size = new Size(300, 22),
                ForeColor = AppTheme.FieldValue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            var debugLogHelp = new Label
            {
                Text = "When on, the Log tab shows full diagnostic detail and the background " +
                       "service writes a service.log file. When off (default) only the most " +
                       "meaningful messages are shown and no service.log is written, to avoid " +
                       "constant disk writes.",
                Location = new Point(36, 324),
                Size = new Size(450, 50),
                ForeColor = AppTheme.FieldLabel
            };
            _debugLog.CheckedChanged += OnDebugLogChanged;

            Controls.Add(_portable);
            Controls.Add(_portableHelp);
            Controls.Add(_wgNt);
            Controls.Add(wgNtHelp);
            Controls.Add(_autoStart);
            Controls.Add(_autoStartHelp);
            Controls.Add(_guiAutoStart);
            Controls.Add(_guiAutoStartHelp);
            Controls.Add(_debugLog);
            Controls.Add(debugLogHelp);
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
            bool anyStartup = _autoStart.Checked || _guiAutoStart.Checked;

            // Startup options: usable only when not in portable mode (but a
            // currently-checked one can still be unchecked).
            _autoStart.Enabled = !portable || _autoStart.Checked;
            _autoStartHelp.Enabled = !portable;
            _guiAutoStart.Enabled = !portable || _guiAutoStart.Checked;
            _guiAutoStartHelp.Enabled = !portable;

            // Portable: usable only when no startup option is on (but if it's
            // already on, allow turning it off) — UNLESS this is a proper
            // Program Files install, in which case it's locked off entirely
            // (see InstallLocation.cs): portable mode is for the standalone/
            // zip distribution, not a real install.
            bool installedLocation = WgSharp.Core.InstallLocation.IsInstalled();
            _portable.Enabled = !installedLocation && (!anyStartup || _portable.Checked);
            _portableHelp.Enabled = !installedLocation && !anyStartup;
            if (installedLocation)
                _portableHelp.Text = "Not available: WgSharp was installed via the MSI installer to a " +
                    "fixed location, and portable mode is only for the standalone copy that travels " +
                    "with its own folder. Use the zip distribution instead if you need this.";
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_loading) return;
            try
            {
                if (_autoStart.Checked)
                {
                    ServiceInstaller.Install();
                }
                else
                {
                    ServiceInstaller.Uninstall();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Couldn't " + (_autoStart.Checked ? "install" : "remove") +
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
                MessageBox.Show(this, "Couldn't " + (_guiAutoStart.Checked ? "enable" : "disable") +
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
            if (MessageBox.Show(this, msg, "WgSharp \u2014 portable mode",
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

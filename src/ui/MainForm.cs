using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WgSharp.Core;
using WgSharp.Crypto;

namespace WgSharp.Ui
{
    public partial class MainForm : Form
    {
        private ITunnelBackend _tunnel;
        private Config _config;
        private string _configText = "";
        private string _tunnelName = "wg";
        private bool _active;
        // True while an activate/deactivate is running on the background thread
        // (see ActivateTunnel/DeactivateTunnel). Guards against double-clicking
        // Activate or double-clicking a tunnel in the list while one is already
        // in flight, which could otherwise start two overlapping connect/
        // disconnect sequences against the same _tunnel field.
        private bool _busy;
        // Per-tunnel password cache (portable mode): once the user unlocks or sets
        // a tunnel's password this session, every later action (Activate, Edit,
        // Save, Export) for that same tunnel reuses it instead of prompting again.
        private readonly System.Collections.Generic.Dictionary<string, string> _tunnelPasswords =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _activeTunnelName;   // name of the currently connected tunnel (null if none)
        private StatsPanel _statsPanel;
        private SettingsPanel _settingsPanel;

        // value labels we refresh on each status tick
        private StatusRow _statusRow;
        private Label _valListenPort, _valAddresses, _valDns, _valMtu, _valKeepalive;
        private Label _valPubKey, _valPeerPubKey;
        private Label _valAllowedIps, _valEndpoint, _valHandshake, _valTransfer;

        private ContextMenuStrip _trayMenu;
        // Closing the window normally minimizes to tray instead of exiting (see
        // OnFormClosing below); only the tray menu's Exit item sets this first.
        private bool _exitRequested;
        // When launched at login via the "--tray" argument, the GUI starts
        // hidden in the notification area instead of opening its window. See
        // SetVisibleCore, which suppresses the very first show so the window
        // never flashes on screen at login.
        private readonly bool _startInTray;
        private bool _firstShowHandled;


        public MainForm() : this(false) { }

        public MainForm(bool startInTray)
        {
            _startInTray = startInTray;
            InitializeComponent();
            ApplyStaticTheme();
            ApplyToolbarIcons();

            // Host the live-stats panel in the Stats tab.
            _statsPanel = new StatsPanel();
            tabStats.Controls.Add(_statsPanel);

            // Host the settings panel in the Settings tab.
            _settingsPanel = new SettingsPanel();
            _settingsPanel.LoadFromSettings();
            _settingsPanel.PortableModeChanged += OnPortableModeChanged;
            tabSettings.Controls.Add(_settingsPanel);

            // Load any previously stored tunnels (DPAPI or portable).
            bool loaded = false;
            try
            {
                var names = ConfigStore.List();
                if (names.Count > 0)
                {
                    foreach (string n in names) lstTunnels.Items.Add(n);
                    lstTunnels.SelectedIndex = 0;
                    _tunnelName = names[0];
                    if (ConfigStore.RequiresPassword)
                    {
                        // Portable mode: don't decrypt until the user selects/acts;
                        // show a placeholder so the panes aren't empty.
                        _configText = "# Portable (encrypted) tunnel.\r\n" +
                                      "# Select Activate or Edit to unlock with your password.\r\n";
                    }
                    else
                    {
                        _configText = ConfigStore.Load(_tunnelName);
                    }
                    loaded = true;
                }
            }
            catch { /* fall through to sample */ }

            if (!loaded)
            {
                // No stored tunnels: start with a sample so the panes aren't empty.
                _configText =
                    "[Interface]\r\n" +
                    "PrivateKey = \r\n" +
                    "Address = 10.0.0.2/32\r\n\r\n" +
                    "[Peer]\r\n" +
                    "PublicKey = \r\n" +
                    "Endpoint = vpn.example.com:51820\r\n" +
                    "AllowedIPs = 0.0.0.0/0, ::/0\r\n" +
                    "PersistentKeepalive = 25\r\n";
                lstTunnels.Items.Add(_tunnelName);
                lstTunnels.SelectedIndex = 0;
            }

            TryParseConfig();
            BuildDetail();

            // Pre-authorize this exe with Windows Firewall so the interactive
            // "allow this app" consent dialog never needs to fire (we're already
            // elevated, so this is silent). Synchronous: it's two quick netsh
            // calls, and must complete before the user could possibly connect.
            WgSharp.Tun.FirewallSelfRegister.Log += Log;
            WgSharp.Tun.FirewallSelfRegister.EnsureRulesForCurrentExe();

            // If the background service is installed but registered against
            // a different binPath than this exe's own (e.g. an earlier
            // extraction folder, or a version from before the --service
            // argument existed), fix and restart it now — before anything
            // else talks to it (the tray menu pre-warm and the "is a tunnel
            // already running" check both happen via Load, right after the
            // constructor finishes). Synchronous, same reasoning as the
            // firewall registration above: a couple of quick sc.exe calls.
            try
            {
                if (WgSharp.Core.ServiceInstaller.RefreshIfStale())
                    Log("Background service registration was stale; reinstalled and restarted it.");
            }
            catch (Exception ex) { Log("Background service refresh check failed: " + ex.Message); }

            // Same self-heal for the login-autostart entry: if it's enabled but
            // points at an old exe path (app moved folders), re-point it.
            try { WgSharp.Core.LoginAutostart.RefreshIfStale(); }
            catch (Exception ex) { Log(Logger.DebugMarker + "Login-autostart refresh check failed: " + ex.Message); }

            // Fetch native drivers (wintun.dll always; wireguard.dll for the
            // WireGuardNT backend) at startup, in the background.
            WgSharp.Tun.DriverBootstrap.Log += Log;
            // Route kill-switch logging to the Log tab once (both backends share the
            // static KillSwitch.Log event; subscribing here avoids duplicate hooks
            // from per-connect tunnel instances).
            WgSharp.Tun.KillSwitch.Log += Log;
            WgSharp.Tun.DriverBootstrap.EnsureDriversAsync();

            // Tray icon context menu: status header, quick-connect profile
            // list, then Status/About/Exit — see OnTrayMenuOpening, which
            // rebuilds it fresh every time it's about to be shown so it can
            // never go stale relative to the tunnel list or connection state.
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Opening += OnTrayMenuOpening;
            notifyIcon.ContextMenuStrip = _trayMenu;

            // Pre-warm the menu-building path once, silently, before the form
            // is shown. The first call into any code is always the slow one
            // in .NET — JIT-compiling OnTrayMenuOpening/ShieldBitmap, and GDI+
            // creating its first Graphics/Font/Bitmap objects — which is
            // exactly the noticeable one-time delay before the tray menu
            // first appears; every open after that is fast because all of
            // that work is already done.
            //
            // RunDeferredStartupWork (below) does this, called from
            // OnHandleCreated in MainForm.SystemMenu.cs — NOT from the Load
            // event: when the app starts in the tray (--tray at login), the
            // window is never shown, so Load never fires — but we still need
            // the already-active-tunnel detection (and thus the service-log
            // pump) to run. OnHandleCreated fires as soon as the handle
            // exists, regardless of visibility, so it covers both the normal
            // and the start-hidden cases. A guard makes sure it only runs once.
        }

        private bool _startupWorkDone;

        // Called from OnHandleCreated (see MainForm.SystemMenu.cs, which
        // already overrides it for the system-menu "About" item — a class can
        // only override a given method once across all of its partial-class
        // files, so this lives here as a plain method instead of a second
        // override). Deferred onto the message loop via BeginInvoke so it
        // doesn't run inside handle creation itself.
        private void RunDeferredStartupWork()
        {
            if (_startupWorkDone) return;
            _startupWorkDone = true;
            BeginInvoke(new Action(delegate
            {
                OnTrayMenuOpening(this, null);   // pre-warm the tray menu path
                CheckForRunningServiceTunnel();  // detect a service tunnel + start the log pump
            }));
        }

        private void ApplyToolbarIcons()
        {
            btnAddTunnel.Image = Icons.Add(16);
            btnAddTunnel.ImageAlign = ContentAlignment.MiddleLeft;
            btnAddTunnel.TextAlign = ContentAlignment.MiddleRight;
            btnAddTunnel.TextImageRelation = TextImageRelation.ImageBeforeText;
            btnDelete.Image = Icons.Delete(16);
            btnExport.Image = Icons.Export(16);
            btnQr.Image = Icons.QrGlyph(16);

            var tips = new ToolTip();
            tips.SetToolTip(btnAddTunnel, "Import tunnel(s) from file");
            tips.SetToolTip(btnDelete, "Remove selected tunnel");
            tips.SetToolTip(btnExport, "Export tunnels to ZIP");
            tips.SetToolTip(btnQr, "Show QR code for selected tunnel");
        }

        private void ApplyStaticTheme()
        {
            BackColor = AppTheme.WindowBg;
            pnlDetail.BackColor = AppTheme.PanelBg;
            lstTunnels.BackColor = AppTheme.ListBg;
            lstTunnels.ForeColor = AppTheme.FieldValue;
            txtLog.BackColor = AppTheme.LogBg;
            txtLog.ForeColor = AppTheme.FieldValue;
        }

        private void Log(string msg)
        {
            if (msg == null) return;
            // Drop verbose/diagnostic ("[dbg] "-marked) lines unless the user
            // has turned on Debug log in Settings; otherwise show only the
            // meaningful messages. Strip the marker before display either way.
            if (!Logger.ShouldShow(msg)) return;
            msg = Logger.Strip(msg);

            if (txtLog.InvokeRequired) { txtLog.BeginInvoke(new Action<string>(Log), msg); return; }
            // Component messages already arrive pre-tagged (e.g. "[Tunnel] ...",
            // "[WFP] ..."). Direct UI-originated messages don't carry a tag, so
            // mark those as [App] here rather than touching every call site.
            string tagged = (msg.Length > 0 && msg[0] == '[') ? msg : "[App] " + msg;
            txtLog.AppendText(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff ") + tagged + "\r\n");
        }

        // Appends a line that already carries its own timestamp (forwarded from
        // the background service's in-memory log via RemoteTunnelBackend.
        // PumpServiceLog), so we don't prepend a second timestamp the way Log
        // does. Verbosity is already filtered service-side, so these are shown
        // as-is.
        private void LogRaw(string line)
        {
            if (line == null) return;
            if (txtLog.InvokeRequired) { txtLog.BeginInvoke(new Action<string>(LogRaw), line); return; }
            txtLog.AppendText(line + "\r\n");
        }

        private void TryParseConfig()
        {
            try { _config = Config.Parse(_configText); }
            catch { _config = null; }
        }

        // ---------------- tunnel list owner-draw (shield + name) ----------------
        private void OnDrawTunnelItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            Color back = selected ? AppTheme.ListSelBg : AppTheme.ListBg;
            Color fore = selected ? AppTheme.ListSelText : AppTheme.FieldValue;

            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, e.Bounds);

            string name = lstTunnels.Items[e.Index].ToString();

            // Only the tunnel that is actually connected shows an active shield.
            bool itemActive = _active && name == _activeTunnelName;

            // shield glyph
            int sx = e.Bounds.Left + 8, sy = e.Bounds.Top + (e.Bounds.Height - 16) / 2;
            DrawShield(e.Graphics, sx, sy, itemActive);

            using (var b = new SolidBrush(fore))
                e.Graphics.DrawString(name, lstTunnels.Font, b, e.Bounds.Left + 32, e.Bounds.Top + 6);
        }

        private static void DrawShield(Graphics g, int x, int y, bool active)
        {
            DrawShield(g, x, y,
                active ? Color.FromArgb(0x4C, 0xAF, 0x50) : Color.FromArgb(0xCF, 0xCF, 0xCF),
                active);
        }

        // Same shield glyph, but with an explicit fill color and checkmark flag
        // — used for the tray menu's status item and per-profile entries, which
        // need more than just "active/inactive" (green=connected, amber=
        // negotiating, red=failed, gray=inactive), matching OnStatusTick's
        // shieldColor logic for the Tunnels tab's status row.
        private static void DrawShield(Graphics g, int x, int y, Color fill, bool showCheck)
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                float w = 16, h = 16, cx = x + w / 2f;
                path.AddLine(cx, y + 0.5f, x + w - 1.5f, y + 3f);
                path.AddLine(x + w - 1.5f, y + 3f, x + w - 1.5f, y + h * 0.55f);
                path.AddBezier(x + w - 1.5f, y + h * 0.55f, x + w - 1.5f, y + h * 0.8f, cx, y + h - 1f, cx, y + h - 1f);
                path.AddBezier(cx, y + h - 1f, cx, y + h - 1f, x + 1.5f, y + h * 0.8f, x + 1.5f, y + h * 0.55f);
                path.AddLine(x + 1.5f, y + h * 0.55f, x + 1.5f, y + 3f);
                path.AddLine(x + 1.5f, y + 3f, cx, y + 0.5f);
                path.CloseFigure();
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (showCheck)
                    using (var p = new Pen(Color.White, 1.6f))
                    {
                        p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        g.DrawLines(p, new[]
                        {
                            new PointF(x + w * 0.30f, y + h * 0.50f),
                            new PointF(x + w * 0.44f, y + h * 0.64f),
                            new PointF(x + w * 0.70f, y + h * 0.34f)
                        });
                    }
            }
        }

        // Renders a shield glyph to a standalone bitmap, sized for a context
        // menu item's Image property (the tray menu's status header and
        // per-profile entries use this).
        private static Bitmap ShieldBitmap(Color fill, bool showCheck)
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                DrawShield(g, 0, 0, fill, showCheck);
            }
            return bmp;
        }

        private void OnTunnelSelected(object sender, EventArgs e)
        {
            if (lstTunnels.SelectedIndex < 0) return;
            string name = lstTunnels.Items[lstTunnels.SelectedIndex].ToString();
            if (name == _tunnelName) return;
            // Viewing a different tunnel is always allowed, even while another is
            // connected; we just load its config for display. The running tunnel
            // keeps going in the background.
            LoadSelectedTunnel();
        }

        // Double-clicking a tunnel name toggles its connection: activates it if
        // it's not the running tunnel, or deactivates it if it is. Uses the
        // system's own double-click timing/distance (MouseDoubleClick only fires
        // for genuine double-clicks), so a fast double-click is exactly what
        // triggers this.
        private void OnTunnelDoubleClick(object sender, MouseEventArgs e)
        {
            int index = lstTunnels.IndexFromPoint(e.Location);
            if (index < 0) return;
            string name = lstTunnels.Items[index].ToString();

            // Make sure the double-clicked tunnel is the one loaded/selected
            // before toggling, in case it wasn't already.
            if (lstTunnels.SelectedIndex != index) lstTunnels.SelectedIndex = index;
            if (_tunnelName != name) LoadSelectedTunnel();

            OnActivateToggle(sender, e);
        }

        // ---------------- drag & drop import onto the tunnel list ----------------
        private void OnTunnelListDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void OnTunnelListDragDrop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            string[] dropped = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (dropped == null || dropped.Length == 0) return;

            // Only hand off files with extensions we know how to import; silently
            // skip anything else (e.g. if other file types were dropped alongside).
            var usable = new System.Collections.Generic.List<string>();
            foreach (string path in dropped)
            {
                string lower = path.ToLowerInvariant();
                if (lower.EndsWith(".conf") || lower.EndsWith(".zip") || lower.EndsWith(".wgsp"))
                    usable.Add(path);
            }
            if (usable.Count == 0)
            {
                Log("Drag-and-drop: no .conf, .zip, or .wgsp files in the dropped items.");
                return;
            }
            ImportFiles(usable.ToArray());
        }

        // ---------------- build the Interface/Peer field rows ----------------
        private void BuildDetail()
        {
            grpInterface.Controls.Clear();
            grpPeer.Controls.Clear();
            grpInterface.ForeColor = AppTheme.GroupText;
            grpPeer.ForeColor = AppTheme.GroupText;

            // ---- Interface group ----
            var gi = FieldGrid.Create();

            _statusRow = new StatusRow();
            _statusRow.Set("Inactive", Color.FromArgb(0xB0, 0xB0, 0xB0), false);
            FieldGrid.AddCustomRow(gi, "Status", _statusRow);

            string pub = "(set private key)";
            if (_config != null && _config.PrivateKey != null)
                pub = FieldGrid.FormatKey(Curve25519.ScalarMultBase(_config.PrivateKey));
            _valPubKey = FieldGrid.AddKeyRow(gi, "Public key", pub);

            // Listen port: only show if explicitly set (the official app shows the
            // ephemeral port once active; we show the configured one when present).
            if (_config != null && _config.ListenPort > 0)
                _valListenPort = FieldGrid.AddRow(gi, "Listen port", _config.ListenPort.ToString());

            string addr = (_config != null && !string.IsNullOrEmpty(_config.Address)) ? _config.Address : "\u2014";
            _valAddresses = FieldGrid.AddRow(gi, "Addresses", addr);

            // DNS row only appears when DNS is configured (dynamic, like the original).
            if (_config != null && !string.IsNullOrEmpty(_config.Dns))
                _valDns = FieldGrid.AddRow(gi, "DNS servers", _config.Dns);
            else
                _valDns = null;

            // MTU row only appears when explicitly set in the config (0 means
            // "auto", derived at connect time from the default-route interface,
            // so there's nothing meaningful to show until then).
            if (_config != null && _config.Mtu > 0)
                _valMtu = FieldGrid.AddRow(gi, "MTU", _config.Mtu.ToString());
            else
                _valMtu = null;

            grpInterface.Controls.Add(gi);

            // ---- Peer group ----
            var gp = FieldGrid.Create();
            string peerPub = "\u2014", allowed = "\u2014", endpoint = "\u2014";
            if (_config != null)
            {
                if (_config.PeerPublicKey != null) peerPub = FieldGrid.FormatKey(_config.PeerPublicKey);
                allowed = _config.AllowedIPs.Count > 0 ? string.Join(", ", _config.AllowedIPs.ToArray()) : "\u2014";
                endpoint = string.IsNullOrEmpty(_config.Endpoint) ? "\u2014" : _config.Endpoint;
            }
            _valPeerPubKey = FieldGrid.AddKeyRow(gp, "Public key", peerPub);
            _valAllowedIps = FieldGrid.AddRow(gp, "Allowed IPs", allowed);
            _valEndpoint = FieldGrid.AddRow(gp, "Endpoint", endpoint);

            // Persistent keepalive: only when configured (dynamic).
            if (_config != null && _config.PersistentKeepalive > 0)
                _valKeepalive = FieldGrid.AddRow(gp, "Persistent keepalive", _config.PersistentKeepalive.ToString());
            else
                _valKeepalive = null;

            // Latest handshake and transfer only appear while THIS tunnel is the
            // active connection (like the official app, which hides them when
            // inactive). They're populated by status ticks.
            bool showLive = _active && _tunnelName == _activeTunnelName;
            if (showLive)
            {
                _valHandshake = FieldGrid.AddRow(gp, "Latest handshake", "\u2014");
                _valTransfer = FieldGrid.AddRow(gp, "Transfer", "\u2014");
            }
            else
            {
                _valHandshake = null;
                _valTransfer = null;
            }
            grpPeer.Controls.Add(gp);

            // Theme the detail surface.
            pnlDetail.BackColor = AppTheme.PanelBg;

            // A small, left-aligned Activate button (like the official app) living
            // in a thin host strip between the two groups.
            var actHost = new Panel();
            actHost.Dock = DockStyle.Top;
            actHost.Height = 40;
            actHost.BackColor = Color.Transparent;
            btnActivate.Dock = DockStyle.None;
            btnActivate.Size = new Size(96, 26);
            btnActivate.Location = new Point(6, 7);
            actHost.Controls.Add(btnActivate);

            // Rebuild the top-docked stack: Peer, Activate strip, Interface (reverse add).
            pnlDetail.Controls.Clear();
            pnlDetail.Controls.Add(grpPeer);
            pnlDetail.Controls.Add(actHost);
            pnlDetail.Controls.Add(grpInterface);
            pnlDetail.Controls.Add(btnEdit);
            btnEdit.BringToFront();
            PositionEditButton();

            UpdateActivateButton();
        }

        private void PositionEditButton()
        {
            if (btnEdit == null) return;
            // Match pnlButtons' bottom margin exactly (Height=38, button Y=6,
            // height=26 => 38-6-26=6px from its panel's bottom edge) so Edit
            // lines up with Add Tunnel/Delete/Export/QR on the other side of
            // the splitter instead of sitting a few pixels higher.
            btnEdit.Location = new Point(pnlDetail.ClientSize.Width - btnEdit.Width - 16,
                                         pnlDetail.ClientSize.Height - btnEdit.Height - 6);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (btnEdit != null) PositionEditButton();
        }

        // ---------------- activate / deactivate ----------------
        private void UpdateActivateButton()
        {
            bool displayedIsActive = _active && _tunnelName == _activeTunnelName;
            btnActivate.Text = displayedIsActive ? "Deactivate" : "Activate";
        }

        // Keeps the system tray icon's hover tooltip current with the actually
        // running tunnel's state — independent of which tunnel/tab is currently
        // displayed, same as the Stats tab. state is null/omitted for "nothing
        // running" (after Deactivate or a failed Activate).
        private void UpdateTrayTooltip(string state)
        {
            if (notifyIcon == null) return;
            string text;
            if (string.IsNullOrEmpty(state) || state == "Idle")
            {
                text = "WgSharp \u2014 Inactive";
            }
            else
            {
                string label = state == "Connected" ? "Connected" : FriendlyState(state);
                text = "WgSharp \u2014 " + label +
                       (string.IsNullOrEmpty(_activeTunnelName) ? "" : " (" + _activeTunnelName + ")");
            }
            // NotifyIcon.Text has a long-standing ~63-character practical limit
            // on Windows; truncate defensively rather than let it silently fail.
            if (text.Length > 63) text = text.Substring(0, 60) + "...";
            notifyIcon.Text = text;
        }

        private void OnTrayIconDoubleClick(object sender, EventArgs e)
        {
            ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        // Rebuilt fresh every time the tray menu is about to open, so it can
        // never go stale relative to the tunnel list or connection state —
        // matches the official client's tray menu: a status line, the list of
        // tunnels for one-click switching (with a shield showing which one, if
        // any, is active), then Status/About/Exit.
        private void OnTrayMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            _trayMenu.Items.Clear();

            bool connected = false;
            Color shieldColor = Color.FromArgb(0xCF, 0xCF, 0xCF);
            string stateText = "Inactive";
            if (_tunnel != null)
            {
                TunnelStatus s = _tunnel.GetStatus();
                connected = s.State == "Connected";
                if (connected) shieldColor = Color.FromArgb(0x4C, 0xAF, 0x50);
                else if (s.State == "Failed") shieldColor = Color.FromArgb(0xD0, 0x4A, 0x4A);
                else shieldColor = Color.FromArgb(0xE0, 0xA3, 0x3C);
                stateText = (connected ? "Connected" : FriendlyState(s.State)) +
                            (string.IsNullOrEmpty(_activeTunnelName) ? "" : " (" + _activeTunnelName + ")");
            }
            var header = new ToolStripMenuItem("WgSharp \u2014 " + stateText);
            header.Image = ShieldBitmap(shieldColor, connected);
            header.Enabled = false;
            _trayMenu.Items.Add(header);
            _trayMenu.Items.Add(new ToolStripSeparator());

            // Quick-connect: click any profile name to switch straight to it.
            foreach (object item in lstTunnels.Items)
            {
                string name = item.ToString();
                bool isActive = _active && name == _activeTunnelName;
                var profileItem = new ToolStripMenuItem(name);
                profileItem.Image = ShieldBitmap(
                    isActive ? Color.FromArgb(0x4C, 0xAF, 0x50) : Color.FromArgb(0xCF, 0xCF, 0xCF), isActive);
                profileItem.Tag = name;
                profileItem.Click += OnTrayProfileClicked;
                _trayMenu.Items.Add(profileItem);
            }
            if (lstTunnels.Items.Count > 0) _trayMenu.Items.Add(new ToolStripSeparator());

            var tunnelsItem = new ToolStripMenuItem("Tunnels");
            tunnelsItem.Click += delegate { ShowMainWindow(); tabs.SelectedIndex = 0; };
            _trayMenu.Items.Add(tunnelsItem);

            var statsItem = new ToolStripMenuItem("Stats");
            statsItem.Click += delegate { ShowMainWindow(); tabs.SelectedIndex = 1; };
            _trayMenu.Items.Add(statsItem);

            var settingsItem = new ToolStripMenuItem("Settings");
            settingsItem.Click += delegate { ShowMainWindow(); tabs.SelectedIndex = 2; };
            _trayMenu.Items.Add(settingsItem);

            var logItem = new ToolStripMenuItem("Log");
            logItem.Click += delegate { ShowMainWindow(); tabs.SelectedIndex = 3; };
            _trayMenu.Items.Add(logItem);

            var aboutItem = new ToolStripMenuItem("About");
            aboutItem.Click += delegate { using (var dlg = new AboutDialog()) dlg.ShowDialog(); };
            _trayMenu.Items.Add(aboutItem);

            _trayMenu.Items.Add(new ToolStripSeparator());

            var disconnectItem = new ToolStripMenuItem("Disconnect");
            disconnectItem.Enabled = _active && !_busy; // nothing to disconnect, or already mid-operation
            disconnectItem.Click += delegate { if (_active && !_busy) DeactivateTunnel(); };
            _trayMenu.Items.Add(disconnectItem);

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { _exitRequested = true; Close(); };
            _trayMenu.Items.Add(exitItem);
        }

        // Switches directly to the clicked tunnel (matching the official
        // client's tray behavior): loads it if it wasn't already displayed,
        // then activates it. A no-op if it's already the running tunnel.
        private void OnTrayProfileClicked(object sender, EventArgs e)
        {
            if (_busy) return;
            string name = (string)((ToolStripMenuItem)sender).Tag;
            if (_active && name == _activeTunnelName) return;
            int idx = lstTunnels.Items.IndexOf(name);
            if (idx < 0) return;
            lstTunnels.SelectedIndex = idx;
            if (_tunnelName != name) LoadSelectedTunnel();
            ActivateTunnel();
        }

        // When started with "--tray" (login autostart), swallow the first
        // request to make the form visible so it never appears on screen at
        // login — it lives in the tray until the user opens it. Application.Run
        // always tries to show the main form once; this intercepts exactly that
        // first show. Every later Show()/ShowMainWindow() works normally.
        protected override void SetVisibleCore(bool value)
        {
            if (!_firstShowHandled)
            {
                _firstShowHandled = true;
                if (_startInTray && value)
                {
                    // Ensure the handle is created (so Load fires, tray icon
                    // appears, status checks run) but keep the window hidden.
                    if (!IsHandleCreated) CreateHandle();
                    base.SetVisibleCore(false);
                    return;
                }
            }
            base.SetVisibleCore(value);
        }

        // Closing the window minimizes to tray instead of exiting, matching the
        // official client; only the tray menu's Exit item actually quits.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        private void OnActivateToggle(object sender, EventArgs e)
        {
            if (_busy) return;
            bool displayedIsActive = _active && _tunnelName == _activeTunnelName;
            if (displayedIsActive) DeactivateTunnel();
            else ActivateTunnel();
        }

        private void ActivateTunnel()
        {
            ActivateTunnel(false);
        }

        private void ActivateTunnel(bool skipUnlock)
        {
            try
            {
                // In portable mode the stored config is password-encrypted; unlock
                // it now (prompting the user) before parsing/activating. The
                // reconnect-after-edit path already holds the decrypted text, so it
                // passes skipUnlock to avoid a redundant password prompt.
                if (ConfigStore.RequiresPassword && !skipUnlock)
                {
                    if (!UnlockPortableConfig()) return; // cancelled or wrong password
                }

                TryParseConfig();
                if (_config == null) throw new Exception("Configuration is invalid.");

                // If the tunnel being activated is already the running one,
                // there's nothing to do — most importantly, do NOT fall into
                // the switch-tunnels branch below, which would DEACTIVATE it
                // first. That exact mis-step is what happened when the service
                // had auto-reconnected a tunnel at boot: the GUI detected it
                // as active on startup, then a click on that same tunnel was
                // read as "switch," tearing down the very tunnel the user
                // wanted and then failing. A no-op is the correct response.
                if (_active && _tunnel != null && _tunnelName == _activeTunnelName)
                {
                    Log("Tunnel '" + _tunnelName + "' is already active.");
                    return;
                }

                // Only one tunnel runs at a time. If a DIFFERENT one's already
                // running (genuinely switching tunnels), tear it down off the
                // UI thread too — via the same DeactivateTunnel used elsewhere
                // — and only start the new one once that's genuinely finished,
                // so the two never race over the same adapter.
                if (_active && _tunnel != null)
                    DeactivateTunnel(BeginActivate);
                else
                    BeginActivate();
            }
            catch (Exception ex)
            {
                _busy = false;
                Log("Activation failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Activation failed",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // The actual bring-up: builds the backend and starts it off the UI
        // thread. _config/_tunnelName must already be the ones to activate by
        // the time this runs (ActivateTunnel guarantees that above).
        // If the background service is already running a tunnel when the GUI
        // starts up — most notably one it auto-reconnected at boot, before
        // anyone logged in — reflect that immediately instead of waiting for
        // the user's next manual click. Runs the IPC off the UI thread since
        // it's startup-time work that shouldn't add to perceived launch time.
        private void CheckForRunningServiceTunnel()
        {
            if (AppSettings.PortableMode) return; // service-driven tunnels are never portable
            ThreadPool.QueueUserWorkItem(delegate
            {
                if (!ServiceClient.IsServiceRunning()) return;
                string resp = ServiceClient.SendCommand("STATUS");
                string name;
                TunnelStatus s = ServiceProtocol.ParseStatus(resp, out name);
                if (s == null || string.IsNullOrEmpty(name)) return;

                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        // _busy guards an in-flight activate/deactivate (set
                        // synchronously the moment one starts, before _active
                        // itself flips), so checking only _active left a
                        // window where this could race a user-initiated
                        // activation and stomp on _tunnel/_activeTunnelName
                        // out from under it.
                        if (_active || _busy) return;
                        var tunnel = new RemoteTunnelBackend(name);
                        tunnel.LogMessage += Log;
                        tunnel.ServiceLogLine += LogRaw;
                        _tunnel = tunnel;
                        _active = true;
                        _activeTunnelName = name;
                        if (lstTunnels.Items.IndexOf(name) < 0) lstTunnels.Items.Add(name);
                        // Select and display the detected tunnel too, so
                        // _tunnelName (the displayed tunnel) matches
                        // _activeTunnelName. Without this they can differ,
                        // which makes the Activate/Deactivate button show
                        // "Activate" for an already-active tunnel — and a
                        // click on it then gets misread as switching tunnels.
                        int didx = lstTunnels.Items.IndexOf(name);
                        if (didx >= 0 && lstTunnels.SelectedIndex != didx)
                            lstTunnels.SelectedIndex = didx; // fires LoadSelectedTunnel -> sets _tunnelName
                        _tunnelName = name;
                        UpdateActivateButton();
                        BuildDetail();
                        if (_statsPanel != null) _statsPanel.SessionReset(true);
                        statusTimer.Start();
                        lstTunnels.Invalidate();
                        Log("Detected an already-active tunnel '" + name + "' from the background service.");
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void BeginActivate()
        {
            string tunnelNameSnapshot = _tunnelName;
            ITunnelBackend tunnel;

            // If the background service is installed, route activation through
            // it: activating becomes "start the service" (which brings the
            // tunnel up inside the service, as a LocalSystem process that
            // survives logout and can reconnect before login). The service is
            // normally installed-but-stopped when idle, so we check IsInstalled,
            // NOT IsRunning — starting it from stopped is exactly the activate
            // action. Modeled on the official client, where activation is an
            // SCM start of the per-tunnel service. Only non-portable tunnels
            // qualify; portable configs are password-encrypted and the service
            // has no human to type the password, so those always run in-process.
            if (!AppSettings.PortableMode && ServiceInstaller.IsInstalled())
            {
                Log("Background service installed; activating through it.");
                tunnel = new RemoteTunnelBackend(tunnelNameSnapshot);
                ((RemoteTunnelBackend)tunnel).ServiceLogLine += LogRaw;
            }
            else if (AppSettings.UseWireGuardNt)
            {
                Log("Using WireGuardNT (kernel) backend.");
                tunnel = new WireGuardNtTunnel(_config);
            }
            else
            {
                tunnel = new Tunnel(_config);
            }
            tunnel.LogMessage += Log;

            _busy = true;
            btnActivate.Enabled = false;
            btnActivate.Text = "Activating\u2026";
            Log("Activating tunnel '" + tunnelNameSnapshot + "'\u2026");

            // Start() does dozens of blocking calls — driver load, adapter
            // create, DNS/route/MTU setup, firewall self-registration,
            // kill-switch engagement — each a netsh/driver round trip that
            // can take 50-300ms. Run it off the UI thread so the window
            // doesn't stutter or freeze while connecting.
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception failure = null;
                try { tunnel.Start(); }
                catch (Exception ex) { failure = ex; }

                // The app may have closed while Start() was still running on
                // this background thread; calling BeginInvoke on a disposed
                // form throws, and there's nothing left to update anyway.
                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        btnActivate.Enabled = true;
                        if (failure != null)
                        {
                            btnActivate.Text = "Activate";
                            try { tunnel.Stop(); } catch { } // best-effort cleanup of a half-started tunnel
                            UpdateTrayTooltip(null);
                            Log("Activation failed: " + failure.Message);
                            MessageBox.Show(this, failure.Message, "Activation failed",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                        _tunnel = tunnel;
                        _active = true;
                        _activeTunnelName = tunnelNameSnapshot;
                        UpdateActivateButton();
                        UpdateTrayTooltip("Starting");
                        BuildDetail();           // re-render to add the live handshake/transfer rows
                        if (_statsPanel != null) _statsPanel.SessionReset(true);
                        statusTimer.Start();
                        lstTunnels.Invalidate();
                        Log("Tunnel '" + tunnelNameSnapshot + "' activated.");
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        private void DeactivateTunnel()
        {
            DeactivateTunnel(null);
        }

        /// <summary>
        /// Deactivate the running tunnel. Stop() runs off the UI thread (see the
        /// comment in ActivateTunnel), so this returns immediately; onComplete,
        /// if given, runs on the UI thread once teardown has actually finished —
        /// used by the reconnect-after-edit flow below, which must not start the
        /// new tunnel until the old one has genuinely torn down (otherwise both
        /// would race to create the same adapter).
        /// </summary>
        private void DeactivateTunnel(Action onComplete)
        {
            if (_tunnel == null) { if (onComplete != null) onComplete(); return; }
            ITunnelBackend tunnel = _tunnel;
            statusTimer.Stop();
            _busy = true;
            btnActivate.Enabled = false;
            btnActivate.Text = "Deactivating\u2026";

            // Stop() also does blocking netsh/driver work (kill-switch teardown,
            // adapter close) — run it off the UI thread for the same reason
            // Start() is: so the window doesn't stutter while disconnecting.
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { tunnel.Stop(); }
                catch (Exception ex) { Log("Stop error: " + ex.Message); }

                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        _busy = false;
                        btnActivate.Enabled = true;
                        _tunnel = null;
                        _active = false;
                        _activeTunnelName = null;
                        UpdateActivateButton();
                        UpdateTrayTooltip(null);
                        BuildDetail();           // re-render to remove the live rows
                        if (_statsPanel != null) _statsPanel.SessionReset(false);
                        lstTunnels.Invalidate();
                        if (onComplete != null) onComplete();
                    }));
                }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
            });
        }

        // ---------------- live status ----------------
        private void OnStatusTick(object sender, EventArgs e)
        {
            if (_tunnel == null) return;

            // If the running tunnel is service-driven, pull any new lines from
            // the service's in-memory log into our Log tab (no service.log file
            // needed). Cheap STATUS-class pipe call; done off the tick's hot
            // path is unnecessary since it returns immediately.
            var remote = _tunnel as RemoteTunnelBackend;
            if (remote != null)
            {
                try { remote.PumpServiceLog(); } catch { }
            }

            // The list shield always reflects live state...
            lstTunnels.Invalidate();

            // Always feed the Stats tab (it tracks the running tunnel regardless of
            // which tunnel is currently displayed in the Tunnels tab).
            var live = _tunnel.GetStatus();
            if (_statsPanel != null) _statsPanel.UpdateStats(live);
            UpdateTrayTooltip(live.State);

            // ...but the detail panel only shows live stats when the tunnel being
            // viewed is the one that's actually running. If the user navigated to
            // a different (inactive) tunnel, its panel stays in the inactive state.
            if (_tunnelName != _activeTunnelName) return;

            var s = live;
            bool connected = s.State == "Connected";

            Color shieldColor;
            if (connected) shieldColor = Color.FromArgb(0x4C, 0xAF, 0x50);          // green
            else if (s.State == "Failed") shieldColor = Color.FromArgb(0xD0, 0x4A, 0x4A); // red
            else shieldColor = Color.FromArgb(0xE0, 0xA3, 0x3C);                    // amber (negotiating)

            if (_statusRow != null)
                _statusRow.Set(connected ? "Active" : FriendlyState(s.State), shieldColor, connected);
            if (_valHandshake != null)
            {
                if (s.LastHandshakeTime == DateTime.MinValue)
                    _valHandshake.Text = "\u2014";
                else
                {
                    TimeSpan ago = DateTime.Now - s.LastHandshakeTime;
                    if (ago.TotalSeconds < 0) ago = TimeSpan.Zero;
                    _valHandshake.Text = FormatElapsed(ago) + " ago";
                }
            }
            if (_valTransfer != null)
                _valTransfer.Text = FormatBytes(s.RxBytes) + " received, " + FormatBytes(s.TxBytes) + " sent";
            if (_valEndpoint != null) _valEndpoint.Text = s.Endpoint;
            lstTunnels.Invalidate();
        }

        private static string FriendlyState(string state)
        {
            switch (state)
            {
                case "Starting": return "Activating\u2026";
                case "Handshaking": return "Negotiating\u2026";
                case "Connected": return "Active";
                case "Failed": return "Failed";
                default: return "Inactive";
            }
        }

        private static string FormatElapsed(TimeSpan ts)
        {
            // HH:MM:SS, where hours can exceed 24 (total hours).
            long h = (long)ts.TotalHours;
            return h.ToString("00") + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00");
        }

        private static string FormatBytes(long bytes)
        {
            string[] u = { "B", "KiB", "MiB", "GiB", "TiB" };
            double v = bytes; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0") : v.ToString("0.00")) + " " + u[i];
        }

        // ---------------- import / delete / edit ----------------
        // ---------------- settings reactions ----------------

        /// <summary>
        /// Decrypt the currently selected portable tunnel into _configText, asking
        /// the user for the password (with retry). Returns false if cancelled.
        /// </summary>
        /// <summary>
        /// Prompt for an existing tunnel's password, retrying on a wrong password
        /// (verified by actually decrypting it), until success or cancel.
        /// </summary>
        private string PromptExistingPasswordWithRetry(string tunnelName, string prompt)
        {
            while (true)
            {
                string pw = PasswordDialog.AskExisting(this, prompt);
                if (pw == null) return null;
                try { ConfigStore.Load(tunnelName, pw); return pw; }
                catch
                {
                    if (MessageBox.Show(this, "Incorrect password. Try again?", "WgSharp",
                            MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                        return null;
                }
            }
        }

        private bool UnlockPortableConfig()
        {
            // Already unlocked this tunnel this session — reuse it silently.
            string cached;
            if (_tunnelPasswords.TryGetValue(_tunnelName, out cached))
            {
                try { _configText = ConfigStore.Load(_tunnelName, cached); return true; }
                catch { _tunnelPasswords.Remove(_tunnelName); /* stale; fall through to re-prompt */ }
            }

            while (true)
            {
                string pw = PasswordDialog.AskExisting(this,
                    "Enter the password for portable tunnel \"" + _tunnelName + "\".");
                if (pw == null) return false;
                try
                {
                    _configText = ConfigStore.Load(_tunnelName, pw);
                    _tunnelPasswords[_tunnelName] = pw;
                    return true;
                }
                catch
                {
                    if (MessageBox.Show(this, "Incorrect password. Try again?", "WgSharp",
                            MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                        return false;
                }
            }
        }


        private void OnPortableModeChanged()
        {
            // The store location changed; reload the tunnel list from the new store.
            // If a tunnel is currently active, leave it running but warn.
            if (_active)
                Log("Note: store changed while a tunnel is active; it keeps running.");

            lstTunnels.Items.Clear();
            try
            {
                var names = ConfigStore.List();
                foreach (string n in names) lstTunnels.Items.Add(n);
            }
            catch (Exception ex) { Log("Could not list tunnels: " + ex.Message); }

            if (lstTunnels.Items.Count > 0)
            {
                lstTunnels.SelectedIndex = 0;
                _tunnelName = lstTunnels.Items[0].ToString();
                if (ConfigStore.RequiresPassword)
                    _configText = "# Portable (encrypted) tunnel.\r\n" +
                                  "# Select Activate or Edit to unlock with your password.\r\n";
                else
                    try { _configText = ConfigStore.Load(_tunnelName); }
                    catch { _configText = ""; }
            }
            else
            {
                _tunnelName = "tunnel";
                _configText = "";
            }
            TryParseConfig();
            BuildDetail();
            Log(AppSettings.PortableMode
                ? "Portable mode ON \u2014 configs stored next to the app, password-encrypted."
                : "Portable mode OFF \u2014 configs stored in ProgramData (DPAPI).");
        }

        // The Add Tunnel button opens a small menu, mirroring the official app's
        // split button: import from file, or create a new empty tunnel.
        private void OnAddTunnelClicked(object sender, EventArgs e)
        {
            var menu = new ContextMenuStrip();
            var importItem = new ToolStripMenuItem("Import tunnel(s) from file\u2026");
            importItem.Click += new EventHandler(OnLoadClicked);
            var scanItem = new ToolStripMenuItem("Scan from QR code");
            scanItem.Click += new EventHandler(OnScanQrClicked);
            var emptyItem = new ToolStripMenuItem("Add empty tunnel\u2026");
            emptyItem.Click += new EventHandler(OnAddEmptyTunnel);
            menu.Items.Add(importItem);
            menu.Items.Add(scanItem);
            menu.Items.Add(emptyItem);
            // drop the menu just below the button
            menu.Show(btnAddTunnel, new Point(0, btnAddTunnel.Height));
        }

        // Opens the webcam QR scanner; a successful scan lands here with the
        // decoded text (the same plain .conf content the QR was generated
        // from) and is imported exactly like a pasted/typed config.
        private void OnScanQrClicked(object sender, EventArgs e)
        {
            string scanned;
            using (var dlg = new QrScanDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                scanned = dlg.DecodedText;
            }
            if (string.IsNullOrEmpty(scanned)) return;
            ImportScannedConfigText(scanned);
        }

        /// <summary>
        /// Imports a tunnel from QR-scanned config text — the webcam-scanning
        /// counterpart to ImportFiles, minus the "read from a file" part: same
        /// validation, same portable-mode password prompt, same store/list/
        /// select sequence. Prompts for a tunnel name since a QR code (unlike
        /// a .conf file) doesn't carry a filename to derive one from.
        /// </summary>
        private void ImportScannedConfigText(string text)
        {
            try { WgSharp.Core.Config.Parse(text); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "The scanned QR code doesn't look like a valid WireGuard " +
                    "config:\n\n" + ex.Message, "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string suggested = ConfigStore.SanitizeName("ScannedTunnel");
            string name = TextInputDialog.Ask(this, "Name this tunnel",
                "Name for the scanned tunnel:", suggested);
            if (name == null) return; // cancelled
            name = ConfigStore.SanitizeName(name);
            if (name.Length == 0) name = suggested;

            if (lstTunnels.Items.IndexOf(name) >= 0)
            {
                if (MessageBox.Show(this, "A tunnel named '" + name + "' already exists. Overwrite it?",
                        "WgSharp", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            string storePw = null;
            if (ConfigStore.RequiresPassword)
            {
                storePw = PasswordDialog.AskNew(this,
                    "Portable mode: set the password to encrypt the scanned tunnel.");
                if (storePw == null) { Log("Scan import cancelled (no password)."); return; }
                _tunnelPasswords[name] = storePw;
            }

            ConfigStore.Save(name, text, storePw);
            if (lstTunnels.Items.IndexOf(name) < 0) lstTunnels.Items.Add(name);
            int idx = lstTunnels.Items.IndexOf(name);
            if (idx >= 0) lstTunnels.SelectedIndex = idx;
            _tunnelName = name;
            _configText = ConfigStore.RequiresPassword
                ? "# Portable (encrypted) tunnel.\r\n# Select Activate or Edit to unlock with your password.\r\n"
                : text;
            TryParseConfig();
            BuildDetail();
            Log("Imported tunnel '" + name + "' from a scanned QR code.");
        }

        // Create a new tunnel pre-populated with a freshly generated private key,
        // then open the editor for the user to fill in the rest.
        private void OnAddEmptyTunnel(object sender, EventArgs e)
        {
            // Generate a new Curve25519 private key (clamped) and derive its public key.
            byte[] priv = new byte[32];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider())
                rng.GetBytes(priv);
            // clamp per X25519
            priv[0] &= 248; priv[31] &= 127; priv[31] |= 64;
            string privB64 = Convert.ToBase64String(priv);

            string template =
                "[Interface]\r\n" +
                "PrivateKey = " + privB64 + "\r\n" +
                "Address = \r\n" +
                "DNS = \r\n\r\n" +
                "[Peer]\r\n" +
                "PublicKey = \r\n" +
                "AllowedIPs = 0.0.0.0/0, ::/0\r\n" +
                "Endpoint = \r\n" +
                "PersistentKeepalive = 25\r\n";

            // Pick a unique default name.
            string baseName = "tunnel";
            string name = baseName;
            int n = 1;
            while (lstTunnels.Items.IndexOf(name) >= 0) { n++; name = baseName + n; }

            using (var dlg = new EditConfigDialog(name, template, true))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    string newName = string.IsNullOrEmpty(dlg.TunnelName) ? name : dlg.TunnelName;
                    _tunnelName = ConfigStore.SanitizeName(newName);
                    _configText = dlg.ConfigText;

                    string pw = null;
                    if (ConfigStore.RequiresPassword)
                    {
                        pw = PasswordDialog.AskNew(this,
                            "Set a password to encrypt this portable tunnel. You'll need it to " +
                            "activate the tunnel later or to import it on another machine.");
                        if (pw == null) { Log("New tunnel cancelled (no password)."); return; }
                        _tunnelPasswords[_tunnelName] = pw;
                    }

                    try { ConfigStore.Save(_tunnelName, _configText, pw); }
                    catch (Exception ex) { Log("Could not save new tunnel: " + ex.Message); }

                    if (lstTunnels.Items.IndexOf(_tunnelName) < 0)
                        lstTunnels.Items.Add(_tunnelName);
                    lstTunnels.SelectedIndex = lstTunnels.Items.IndexOf(_tunnelName);
                    TryParseConfig();
                    BuildDetail();
                    Log("Created tunnel '" + _tunnelName + "'.");
                }
            }
        }

        private void OnLoadClicked(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Filter = "WireGuard config, archive or WgSharp portable (*.conf;*.zip;*.wgsp)|*.conf;*.zip;*.wgsp|" +
                             "WireGuard config (*.conf)|*.conf|ZIP archive (*.zip)|*.zip|" +
                             "WgSharp portable (*.wgsp)|*.wgsp|All files (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ImportFiles(dlg.FileNames);
            }
        }

        /// <summary>
        /// Import one or more .conf/.zip/.wgsp files (used by both the Import
        /// dialog and drag-and-drop onto the tunnel list). Any portable-mode
        /// password chosen for this batch is cached per imported tunnel name, so
        /// activating/editing/exporting them later this session won't re-prompt.
        /// </summary>
        private void ImportFiles(string[] files)
        {
            if (files == null || files.Length == 0) return;

            // In portable mode, ask once for the password used to (re-)encrypt
            // everything imported in this batch.
            string storePw = null;
            if (ConfigStore.RequiresPassword)
            {
                storePw = PasswordDialog.AskNew(this,
                    "Portable mode: set the password to encrypt the imported tunnel(s).");
                if (storePw == null) { Log("Import cancelled (no password)."); return; }
            }

            int imported = 0;
            string lastName = null;
            var importedNames = new System.Collections.Generic.List<string>();
            foreach (string file in files)
            {
                try
                {
                    string lower = file.ToLowerInvariant();
                    if (lower.EndsWith(".zip"))
                    {
                        foreach (string n in ImportZip(file, storePw)) { lastName = n; importedNames.Add(n); imported++; }
                    }
                    else
                    {
                        string text = ReadConfigFile(file); // handles plain or portable blob
                        if (text == null) continue;          // cancelled password
                        string name = ConfigStore.SanitizeName(Path.GetFileNameWithoutExtension(file));
                        ConfigStore.Save(name, text, storePw);
                        if (lstTunnels.Items.IndexOf(name) < 0) lstTunnels.Items.Add(name);
                        lastName = name; importedNames.Add(name); imported++;
                    }
                }
                catch (Exception ex) { Log("Import of " + Path.GetFileName(file) + " failed: " + ex.Message); }
            }

            // Cache the batch password for every tunnel just imported, so later
            // actions on any of them won't re-prompt this session.
            if (storePw != null)
                foreach (string n in importedNames) _tunnelPasswords[n] = storePw;

            if (imported > 0 && lastName != null)
            {
                int idx = lstTunnels.Items.IndexOf(lastName);
                if (idx >= 0) lstTunnels.SelectedIndex = idx;
                _tunnelName = lastName;
                if (ConfigStore.RequiresPassword)
                    _configText = "# Portable (encrypted) tunnel.\r\n" +
                                  "# Select Activate or Edit to unlock with your password.\r\n";
                else
                    _configText = ConfigStore.Load(lastName);
                TryParseConfig();
                BuildDetail();
                Log("Imported " + imported + " tunnel(s).");
            }
        }

        /// <summary>
        /// Read a config file into plaintext. If it's a WgSharp portable blob
        /// (recognized by the magic header), prompt for its password and decrypt;
        /// otherwise treat it as plain UTF-8 text. Returns null if the user
        /// cancels a password prompt.
        /// </summary>
        private string ReadConfigFile(string file)
        {
            byte[] raw = File.ReadAllBytes(file);
            if (WgSharp.Core.PortableCrypto.IsPortableBlob(raw))
            {
                // Encrypted portable config from some WgSharp client. Prompt for its
                // password and decrypt to plaintext (so it lands on this system in
                // whatever store we're currently using).
                while (true)
                {
                    string pw = PasswordDialog.AskExisting(this,
                        "\"" + Path.GetFileName(file) + "\" is an encrypted WgSharp portable " +
                        "config. Enter its password to import it.");
                    if (pw == null) return null; // cancelled
                    try { return WgSharp.Core.PortableCrypto.Decrypt(raw, pw); }
                    catch
                    {
                        if (MessageBox.Show(this, "Incorrect password. Try again?", "WgSharp",
                                MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning) != DialogResult.Retry)
                            return null;
                    }
                }
            }
            return System.Text.Encoding.UTF8.GetString(raw);
        }

        private System.Collections.Generic.List<string> ImportZip(string zipPath, string storePw)
        {
            var names = new System.Collections.Generic.List<string>();
            using (var zip = System.IO.Compression.ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                {
                    if (!entry.FullName.ToLowerInvariant().EndsWith(".conf")) continue;
                    string name = ConfigStore.SanitizeName(
                        Path.GetFileNameWithoutExtension(entry.Name));
                    string text;
                    using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
                    ConfigStore.Save(name, text, storePw);
                    if (lstTunnels.Items.IndexOf(name) < 0) lstTunnels.Items.Add(name);
                    names.Add(name);
                }
            }
            return names;
        }

        private void OnDeleteClicked(object sender, EventArgs e)
        {
            if (lstTunnels.SelectedIndex < 0 || lstTunnels.Items.Count == 0) return;

            string toRemove = lstTunnels.Items[lstTunnels.SelectedIndex].ToString();

            // Only block deletion of the tunnel that is actually connected.
            if (_active && toRemove == _activeTunnelName)
            {
                MessageBox.Show(this, "Deactivate '" + toRemove + "' before removing it.",
                    "Tunnel active", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this, "Remove tunnel '" + toRemove + "'?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try { ConfigStore.Delete(toRemove); } catch (Exception ex) { Log("Delete error: " + ex.Message); }

            int idx = lstTunnels.SelectedIndex;
            lstTunnels.Items.RemoveAt(idx);
            Log("Removed tunnel '" + toRemove + "'.");

            if (lstTunnels.Items.Count > 0)
            {
                lstTunnels.SelectedIndex = Math.Min(idx, lstTunnels.Items.Count - 1);
                LoadSelectedTunnel();
            }
            else
            {
                _configText = "";
                _config = null;
                _tunnelName = "wg";
                BuildDetail();
            }
        }

        private void LoadSelectedTunnel()
        {
            if (lstTunnels.SelectedIndex < 0) return;
            string name = lstTunnels.Items[lstTunnels.SelectedIndex].ToString();
            try
            {
                _tunnelName = name;
                if (ConfigStore.RequiresPassword)
                {
                    // If we already unlocked this tunnel earlier this session
                    // (Activate/Edit/Save/Export), reuse that password silently
                    // instead of showing the locked placeholder — selecting a
                    // tunnel you've already unlocked shouldn't hide its details.
                    string cachedPw;
                    if (_tunnelPasswords.TryGetValue(name, out cachedPw))
                    {
                        try
                        {
                            _configText = ConfigStore.Load(name, cachedPw);
                            TryParseConfig();
                            BuildDetail();
                            return;
                        }
                        catch { _tunnelPasswords.Remove(name); /* stale; fall through */ }
                    }
                    // Don't prompt for a password just for selecting; show a locked
                    // placeholder. Activate/Edit will unlock on demand.
                    _configText = "# Portable (encrypted) tunnel \"" + name + "\".\r\n" +
                                  "# Use Activate or Edit to unlock it with your password.\r\n";
                    _config = null;
                }
                else
                {
                    _configText = ConfigStore.Load(name);
                    TryParseConfig();
                }
                BuildDetail();
            }
            catch (Exception ex) { Log("Could not load '" + name + "': " + ex.Message); }
        }

        private void OnEditClicked(object sender, EventArgs e)
        {
            // In portable mode, the in-memory text may be the locked placeholder;
            // unlock it so the editor shows the real configuration.
            if (ConfigStore.RequiresPassword && ConfigStore.Exists(_tunnelName))
            {
                if (!UnlockPortableConfig()) return;
            }

            bool currentBlock = _config != null && _config.BlockUntunneled;
            using (var dlg = new EditConfigDialog(_tunnelName, _configText, currentBlock))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    // Was THIS tunnel the live connection before the edit? If so we
                    // reconnect with the new config after saving.
                    bool wasActive = _active && _tunnelName == _activeTunnelName;

                    // The editor has already written the kill-switch state into the
                    // AllowedIPs line, so the config text is the single source of truth.
                    _configText = dlg.ConfigText;
                    string newName = string.IsNullOrEmpty(dlg.TunnelName) ? _tunnelName : dlg.TunnelName;

                    // If the tunnel was renamed, drop the old stored file.
                    if (!string.Equals(newName, _tunnelName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { ConfigStore.Delete(_tunnelName); } catch { }
                    }
                    _tunnelName = newName;

                    if (lstTunnels.Items.Count == 0) lstTunnels.Items.Add(_tunnelName);
                    else lstTunnels.Items[lstTunnels.SelectedIndex < 0 ? 0 : lstTunnels.SelectedIndex] = _tunnelName;

                    TryParseConfig();
                    PersistCurrent();
                    BuildDetail();
                    Log("Configuration saved for '" + _tunnelName + "'.");

                    // Apply changes live: if this tunnel was connected, reconnect it
                    // so the edited config takes effect immediately.
                    if (wasActive)
                    {
                        Log("Reconnecting '" + _tunnelName + "' to apply changes\u2026");
                        DeactivateTunnel(delegate { ActivateTunnel(true); }); // already have decrypted config; no re-prompt
                    }
                }
            }
        }

        private void PersistCurrent()
        {
            try
            {
                if (ConfigStore.RequiresPassword)
                {
                    string pw;
                    if (!_tunnelPasswords.TryGetValue(_tunnelName, out pw))
                    {
                        // First time this tunnel has ever been saved in portable
                        // mode this session: set its password once, then cache it
                        // so later saves/activations/exports don't ask again.
                        pw = PasswordDialog.AskNew(this,
                            "Portable mode: set the password to encrypt \"" + _tunnelName + "\".");
                        if (pw == null) { Log("Save cancelled (no password)."); return; }
                        _tunnelPasswords[_tunnelName] = pw;
                    }
                    ConfigStore.Save(_tunnelName, _configText, pw);
                }
                else
                {
                    ConfigStore.Save(_tunnelName, _configText);
                }
            }
            catch (Exception ex) { Log("Could not save tunnel: " + ex.Message); }
        }

        // ---------------- export tunnels to ZIP ----------------
        private void OnExportClicked(object sender, EventArgs e)
        {
            if (lstTunnels.Items.Count == 0) { Log("No tunnels to export."); return; }
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "ZIP archive (*.zip)|*.zip";
                dlg.FileName = "wgsharp-tunnels.zip";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    if (File.Exists(dlg.FileName)) File.Delete(dlg.FileName);
                    using (var zip = System.IO.Compression.ZipFile.Open(
                        dlg.FileName, System.IO.Compression.ZipArchiveMode.Create))
                    {
                        foreach (object item in lstTunnels.Items)
                        {
                            string name = item.ToString();
                            string text;
                            if (ConfigStore.RequiresPassword)
                            {
                                // Each portable tunnel can have its own password;
                                // reuse the cached one if we already unlocked it
                                // this session, otherwise prompt for THIS tunnel
                                // specifically (with retry), and cache it.
                                string pw;
                                if (!_tunnelPasswords.TryGetValue(name, out pw))
                                {
                                    pw = PromptExistingPasswordWithRetry(name,
                                        "Enter the password for portable tunnel \"" + name + "\" to include it in the export.");
                                    if (pw == null) { Log("Export of '" + name + "' skipped (no password)."); continue; }
                                    _tunnelPasswords[name] = pw;
                                }
                                try { text = ConfigStore.Load(name, pw); }
                                catch { Log("Export of '" + name + "' skipped (decrypt failed)."); continue; }
                            }
                            else
                            {
                                try { text = ConfigStore.Load(name); }
                                catch { continue; }
                            }
                            var entry = zip.CreateEntry(name + ".conf");
                            using (var w = new StreamWriter(entry.Open()))
                                w.Write(text);
                        }
                    }
                    Log("Exported " + lstTunnels.Items.Count + " tunnel(s) to " + dlg.FileName);
                }
                catch (Exception ex)
                {
                    Log("Export failed: " + ex.Message);
                    MessageBox.Show(this, ex.Message, "Export failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // ---------------- QR code ----------------
        private void OnQrClicked(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(_configText) || _config == null)
            {
                MessageBox.Show(this, "Select a valid tunnel first.", "QR code",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (var dlg = new QrDialog(_tunnelName, _configText))
                    dlg.ShowDialog(this);
            }
            catch (Exception ex)
            {
                Log("QR generation failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "QR code", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}

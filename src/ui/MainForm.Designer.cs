using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    partial class MainForm
    {
        private System.ComponentModel.IContainer components = null;

        private TabControl tabs;
        private TabPage tabTunnels;
        private TabPage tabStats;
        private TabPage tabSettings;
        private TabPage tabLog;

        // Tunnels tab
        private SplitContainer split;
        private ListBox lstTunnels;            // left: tunnel list (owner-drawn for shield)
        private Panel lstTunnelsBorder;        // themed 1px border wrapper (native FixedSingle ignores theme)
        private Panel pnlButtons;              // bottom-left action row
        private Button btnAddTunnel;
        private Button btnDelete;
        private Button btnExport;
        private Button btnQr;

        private Panel pnlDetail;               // right: scrollable detail
        private ThemedGroupBox grpInterface;
        private ThemedGroupBox grpPeer;
        private Button btnActivate;            // Activate/Deactivate toggle
        private Button btnEdit;

        // Log tab
        private TextBox txtLog;
        private NotifyIcon notifyIcon;

        // Edit dialog state (config editing happens in a simple modal)
        private System.Windows.Forms.Timer statusTimer;

        protected override void Dispose(bool disposing)
        {
            if (disposing && components != null) components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();

            this.tabs = new TabControl();
            this.tabTunnels = new TabPage();
            this.tabStats = new TabPage();
            this.tabSettings = new TabPage();
            this.tabLog = new TabPage();
            this.split = new SplitContainer();
            this.lstTunnels = new ListBox();
            this.pnlButtons = new Panel();
            this.btnAddTunnel = new Button();
            this.btnDelete = new Button();
            this.btnExport = new Button();
            this.pnlDetail = new Panel();
            this.grpInterface = new ThemedGroupBox();
            this.grpPeer = new ThemedGroupBox();
            this.btnActivate = new Button();
            this.btnEdit = new Button();
            this.txtLog = new TextBox();
            this.statusTimer = new System.Windows.Forms.Timer(this.components);

            // ---- tabs ----
            this.tabs.Dock = DockStyle.Fill;
            // Native TabControl always paints its tab strip via visual styles
            // (light, regardless of BackColor) - owner-draw + the overlay
            // panels built in BuildTabOverlays() cover this.
            this.tabs.Appearance = TabAppearance.FlatButtons;
            this.tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            this.tabs.DrawItem += new DrawItemEventHandler(this.DrawTab);
            this.tabs.Controls.Add(this.tabTunnels);
            this.tabs.Controls.Add(this.tabStats);
            this.tabs.Controls.Add(this.tabSettings);
            this.tabs.Controls.Add(this.tabLog);
            this.tabTunnels.Text = "Tunnels";
            this.tabTunnels.UseVisualStyleBackColor = true;
            this.tabTunnels.Padding = Dpi.Pad(8);
            this.tabStats.Text = "Stats";
            this.tabStats.UseVisualStyleBackColor = true;
            this.tabStats.Padding = Dpi.Pad(8);
            this.tabSettings.Text = "Settings";
            this.tabSettings.UseVisualStyleBackColor = true;
            this.tabSettings.Padding = Dpi.Pad(8);
            this.tabLog.Text = "Log";
            this.tabLog.UseVisualStyleBackColor = true;
            this.tabLog.Padding = Dpi.Pad(8);

            // ---- split (list | detail) ----
            // Order matters: give the control a concrete size and set the min
            // sizes BEFORE SplitterDistance, otherwise WinForms throws
            // InvalidOperationException when the default tiny size can't satisfy
            // the distance + Panel2MinSize constraint (a silent-crash classic).
            this.split.Size = Dpi.Sz(760, 420);
            this.split.Panel1MinSize = Dpi.S(110);
            this.split.Panel2MinSize = Dpi.S(480);       // room for 44-char key + 140px label column
            this.split.SplitterWidth = Dpi.S(6);
            this.split.FixedPanel = FixedPanel.Panel1;
            this.split.SplitterDistance = Dpi.S(226);    // fits all four toolbar buttons
            this.split.Dock = DockStyle.Fill;

            // ---- left: tunnel list ----
            this.lstTunnels.Dock = DockStyle.Fill;
            this.lstTunnels.IntegralHeight = false;
            this.lstTunnels.DrawMode = DrawMode.OwnerDrawFixed;
            this.lstTunnels.ItemHeight = Dpi.S(28);
            this.lstTunnels.Font = new Font("Segoe UI", 9.5F);
            this.lstTunnels.DrawItem += new DrawItemEventHandler(this.OnDrawTunnelItem);
            this.lstTunnels.SelectedIndexChanged += new System.EventHandler(this.OnTunnelSelected);
            this.lstTunnels.MouseDoubleClick += new MouseEventHandler(this.OnTunnelDoubleClick);
            this.lstTunnels.AllowDrop = true;
            this.lstTunnels.DragEnter += new DragEventHandler(this.OnTunnelListDragEnter);
            this.lstTunnels.DragDrop += new DragEventHandler(this.OnTunnelListDragDrop);
            this.lstTunnels.MouseDown += new MouseEventHandler(this.OnTunnelListMouseDown);
            this.lstTunnels.MouseMove += new MouseEventHandler(this.OnTunnelListMouseMove);
            this.lstTunnels.MouseUp += new MouseEventHandler(this.OnTunnelListMouseUp);
            // Native BorderStyle.FixedSingle always renders a fixed color
            // regardless of theme, and isn't exposed as a settable property -
            // wrap in a themed border panel instead (see Ctrl.Bordered).
            this.lstTunnels.BorderStyle = BorderStyle.None;
            this.lstTunnelsBorder = Ctrl.Bordered(this.lstTunnels);
            this.lstTunnelsBorder.Dock = DockStyle.Fill;

            // ---- left bottom: action buttons ----
            this.pnlButtons.Dock = DockStyle.Bottom;
            this.pnlButtons.Height = Dpi.S(38);
            // Four uniform icon-only buttons (Add Tunnel dropped its text -
            // matches Delete/Export/QR's own icon-only look instead of being
            // a different size/shape from the rest of the row), each a bit
            // bigger than the old 30x26, with a uniform 6px gap between all
            // four and vertically centered in the 38px-tall row.
            this.btnAddTunnel.Text = "";
            this.btnAddTunnel.Location = Dpi.Pt(0, 4);
            this.btnAddTunnel.Size = Dpi.Sz(34, 30);
            this.btnAddTunnel.UseVisualStyleBackColor = true;
            this.btnAddTunnel.Click += new System.EventHandler(this.OnAddTunnelClicked);
            this.btnDelete.Text = "";
            this.btnDelete.Location = Dpi.Pt(40, 4);
            this.btnDelete.Size = Dpi.Sz(34, 30);
            this.btnDelete.UseVisualStyleBackColor = true;
            this.btnDelete.Click += new System.EventHandler(this.OnDeleteClicked);
            this.btnExport.Text = "";
            this.btnExport.Location = Dpi.Pt(80, 4);
            this.btnExport.Size = Dpi.Sz(34, 30);
            this.btnExport.UseVisualStyleBackColor = true;
            this.btnExport.Click += new System.EventHandler(this.OnExportClicked);
            this.btnQr = new Button();
            this.btnQr.Text = "";
            this.btnQr.Location = Dpi.Pt(120, 4);
            this.btnQr.Size = Dpi.Sz(34, 30);
            this.btnQr.UseVisualStyleBackColor = true;
            this.btnQr.Click += new System.EventHandler(this.OnQrClicked);

            // ---- right: detail pane ----
            this.pnlDetail.Dock = DockStyle.Fill;
            this.pnlDetail.AutoScroll = true;
            this.pnlDetail.Padding = Dpi.Pad(10, 6, 10, 6);
            this.pnlDetail.BackColor = Color.White;

            this.grpInterface.Text = "Interface";
            this.grpInterface.Dock = DockStyle.Top;
            this.grpInterface.AutoSize = true;
            this.grpInterface.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.grpInterface.Padding = Dpi.Pad(6, 4, 6, 8);
            this.grpInterface.Font = new Font("Segoe UI", 9F);

            this.grpPeer.Text = "Peer";
            this.grpPeer.Dock = DockStyle.Top;
            this.grpPeer.AutoSize = true;
            this.grpPeer.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            this.grpPeer.Padding = Dpi.Pad(6, 4, 6, 8);
            this.grpPeer.Font = new Font("Segoe UI", 9F);

            this.btnActivate.Text = "Activate";
            this.btnActivate.Size = Dpi.Sz(110, 28);
            this.btnActivate.Dock = DockStyle.Top;
            this.btnActivate.UseVisualStyleBackColor = true;
            this.btnActivate.Margin = Dpi.Pad(0, 8, 0, 8);
            this.btnActivate.Click += new System.EventHandler(this.OnActivateToggle);

            this.btnEdit.Text = "Edit";
            this.btnEdit.Size = Dpi.Sz(80, 26);
            this.btnEdit.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            this.btnEdit.UseVisualStyleBackColor = true;
            this.btnEdit.Click += new System.EventHandler(this.OnEditClicked);

            // detail children are added dynamically in BuildDetail()

            this.split.Panel1.Controls.Add(this.lstTunnelsBorder);
            this.split.Panel1.Controls.Add(this.pnlButtons);
            this.pnlButtons.Controls.Add(this.btnAddTunnel);
            this.pnlButtons.Controls.Add(this.btnDelete);
            this.pnlButtons.Controls.Add(this.btnExport);
            this.pnlButtons.Controls.Add(this.btnQr);
            this.split.Panel2.Controls.Add(this.pnlDetail);

            this.tabTunnels.Controls.Add(this.split);

            // ---- Log tab ----
            this.txtLog.Dock = DockStyle.Fill;
            this.txtLog.Multiline = true;
            this.txtLog.ReadOnly = true;
            this.txtLog.ScrollBars = ScrollBars.Vertical;
            this.txtLog.BorderStyle = BorderStyle.None;
            this.txtLog.Font = new Font("Consolas", 9F);
            this.txtLog.BackColor = Color.White;
            this.tabLog.Controls.Add(this.txtLog);

            // ---- status timer ----
            this.statusTimer.Interval = 1000;
            this.statusTimer.Tick += new System.EventHandler(this.OnStatusTick);

            // ---- form ----
            this.AutoScaleMode = AutoScaleMode.None; // layout is scaled by hand via Dpi.S
            this.ClientSize = Dpi.Sz(760, 470);
            this.MinimumSize = Dpi.Sz(740, 420);
            this.Controls.Add(this.tabs);
            this.Text = "WgSharp";
            // The .ico is embedded into WgSharp.exe itself via /win32icon at
            // build time (see build.cmd); pull it back out at runtime rather
            // than shipping/loading a separate file. AppIconLoader rebuilds
            // every embedded frame (not just one small shell-association
            // size like Icon.ExtractAssociatedIcon gives), so the taskbar,
            // Alt+Tab, and title bar all get a crisp frame instead of a
            // stretched 32x32 one.
            this.Icon = AppIconLoader.LoadFullEmbeddedIcon();
            if (this.Icon == null)
            {
                try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                catch { this.Icon = null; }
            }

            // ---- system tray icon ----
            // Tooltip text is kept current with the connection state by
            // UpdateTrayTooltip() in MainForm.cs (called on activate/deactivate
            // and on each status tick). Double-click restores the window.
            this.notifyIcon = new NotifyIcon(this.components);
            this.notifyIcon.Icon = this.Icon;
            this.notifyIcon.Text = "WgSharp \u2014 Inactive";
            this.notifyIcon.Visible = true;
            this.notifyIcon.DoubleClick += new System.EventHandler(this.OnTrayIconDoubleClick);
        }
    }
}

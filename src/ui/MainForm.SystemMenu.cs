using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    // Adds an "About WgSharp..." entry to the window's system menu (the menu you
    // get by right-clicking the title bar or clicking the window icon), mirroring
    // the official client.
    public partial class MainForm
    {
        private const int WM_SYSCOMMAND = 0x0112;
        private const int MF_STRING = 0x0000;
        private const int MF_SEPARATOR = 0x0800;
        private const int SYSMENU_ABOUT_ID = 0x1001;

        [DllImport("user32.dll")]
        private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

        [DllImport("user32.dll")]
        private static extern bool AppendMenu(IntPtr hMenu, int uFlags, int uIDNewItem, string lpNewItem);

        private void InstallSystemMenu()
        {
            IntPtr sysMenu = GetSystemMenu(this.Handle, false);
            AppendMenu(sysMenu, MF_SEPARATOR, 0, string.Empty);
            AppendMenu(sysMenu, MF_STRING, SYSMENU_ABOUT_ID, "About WgSharp\u2026");
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_SYSCOMMAND && (int)m.WParam == SYSMENU_ABOUT_ID)
            {
                using (var dlg = new AboutDialog())
                    dlg.ShowDialog(this);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            InstallSystemMenu();
            // See MainForm.cs: detects an already-active service tunnel and
            // starts the service-log pump. Must run from here (not Load) so it
            // still happens when the window starts hidden in the tray.
            RunDeferredStartupWork();
        }
    }
}

using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace WgSharp.Ui
{
    // P/Invoke helpers for dark-mode window chrome (title bar, window border,
    // native scrollbars). Ported as-is from LenovoRepoBuilder's
    // NativeMethods.cs — see that project's CLAUDE.md "Visual overhaul"
    // section for the live-tested history behind these specific attribute
    // numbers/ordinals.
    internal static class NativeMethods
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string pszSubIdList);

        // Undocumented, ordinal-only (stable since Win10 1903 for these three -
        // AllowDarkModeForWindow=133, SetPreferredAppMode=135, FlushMenuThemes=136,
        // per the well-known uxtheme.dll ordinal table) - best-effort, wrapped in
        // try/catch by every caller here.
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern bool AllowDarkModeForWindow(IntPtr hWnd, bool allow);

        [DllImport("uxtheme.dll", EntryPoint = "#135", SetLastError = true)]
        static extern int SetPreferredAppModeInternal(int preferredAppMode);

        [DllImport("uxtheme.dll", EntryPoint = "#136")]
        static extern void FlushMenuThemesInternal();

        // Applies dark OR light scrollbar theming to one native scrollable
        // control (ListBox/TextBox/RichTextBox). Order matters: call
        // AllowDarkModeForWindow before SetWindowTheme. Best-effort since
        // this is an undocumented API. Must be called again on every theme
        // toggle, not just once at handle creation.
        public static void SetScrollBarTheme(IntPtr hwnd, bool dark)
        {
            try { AllowDarkModeForWindow(hwnd, dark); } catch { }
            try { SetWindowTheme(hwnd, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        // Call once at startup, before Application.EnableVisualStyles().
        public static void SetPreferredAppMode(bool dark)
        {
            try { SetPreferredAppModeInternal(dark ? 1 : 0); } catch { }
        }

        public static void FlushMenuThemes()
        {
            try { FlushMenuThemesInternal(); } catch { }
        }

        // DWMWA_USE_IMMERSIVE_DARK_MODE - documented, but only actually
        // honored on Windows 10 1809+. The attribute number changed once:
        // 19 on early 1809-era builds, 20 from Windows 10 20H1 onward
        // (including all of Windows 11) - try 20 first, fall back to 19.
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static void SetDarkTitleBar(IntPtr hwnd, bool dark)
        {
            int v = dark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref v, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref v, sizeof(int));
            }
            catch { }
        }

        // COLORREF is 0x00BBGGRR, not RGB - byte order matters here.
        static int ToColorRef(Color c) { return c.R | (c.G << 8) | (c.B << 16); }

        // Windows 11 only (silently no-ops on Windows 10). Pass Color.Empty
        // for either parameter to reset that one back to the system default.
        public static void SetBorderAndCaptionColor(IntPtr hwnd, Color border, Color caption)
        {
            const int DWMWA_BORDER_COLOR = 34;
            const int DWMWA_CAPTION_COLOR = 35;
            const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
            try
            {
                int b = border.IsEmpty ? DWMWA_COLOR_DEFAULT : ToColorRef(border);
                DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref b, sizeof(int));
            }
            catch { }
            try
            {
                int c = caption.IsEmpty ? DWMWA_COLOR_DEFAULT : ToColorRef(caption);
                DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref c, sizeof(int));
            }
            catch { }
        }
    }
}

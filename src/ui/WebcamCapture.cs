using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Drives a webcam through the legacy Video for Windows capture API
    /// (avicap32.dll) — the one capture path available to a .NET Framework
    /// 4.8 WinForms app with no external packages and no Media Foundation COM
    /// interop layer to hand-write. avicap32 is part of Windows itself (no
    /// redistributable needed) and most UVC webcam drivers still register the
    /// compatibility shim it needs, but this is NOT guaranteed for every
    /// camera/driver — some newer or IR/Windows-Hello-only cameras don't
    /// expose it. QrScanDialog treats "no driver found" as a normal,
    /// non-fatal outcome and falls back to its "scan from an image file"
    /// option, since this really is best-effort.
    ///
    /// The capture window is created as a CHILD of a host control and told to
    /// preview directly into it (WM_CAP_SET_PREVIEW), so the live video shows
    /// up with zero rendering code on our side. To get a still frame to feed
    /// the QR decoder, we grab one (WM_CAP_GRAB_FRAME) and copy it to the
    /// clipboard as a DIB (WM_CAP_COPY), then read it back as a Bitmap — the
    /// standard trick for this API; there's no in-process "give me the bytes"
    /// call without a frame-callback (which needs raw DIB parsing for little
    /// extra benefit here, since we only need a frame every second or so).
    /// </summary>
    public sealed class WebcamCapture : IDisposable
    {
        private const int WM_CAP_DRIVER_CONNECT = 0x40A;
        private const int WM_CAP_DRIVER_DISCONNECT = 0x40B;
        private const int WM_CAP_SET_PREVIEW = 0x432;
        private const int WM_CAP_SET_PREVIEWRATE = 0x434;
        private const int WM_CAP_SET_SCALE = 0x435;
        private const int WM_CAP_GRAB_FRAME = 0x43C; // WM_CAP_GRAB_FRAME_NOSTOP
        private const int WM_CAP_COPY = 0x43D; // WM_CAP_EDIT_COPY

        [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr capCreateCaptureWindowA(string lpszWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, int nID);

        [DllImport("avicap32.dll")]
        private static extern bool capGetDriverDescriptionA(short wDriverIndex,
            byte[] lpszName, int cbName, byte[] lpszVer, int cbVer);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, int wParam, int lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;

        private IntPtr _hwnd;
        private bool _connected;

        /// <summary>
        /// True if at least one capture driver is registered with the system.
        /// Checked before showing the scan dialog's webcam mode at all, so a
        /// machine with no recognized capture device goes straight to the
        /// file-based fallback instead of a window that can't possibly work.
        /// </summary>
        public static bool AnyDriverAvailable()
        {
            try
            {
                byte[] name = new byte[100];
                byte[] ver = new byte[100];
                for (short i = 0; i < 10; i++)
                    if (capGetDriverDescriptionA(i, name, name.Length, ver, ver.Length))
                        return true;
            }
            catch { /* avicap32 missing entirely (shouldn't happen on real Windows) */ }
            return false;
        }

        /// <summary>
        /// Creates the capture window as a child of <paramref name="host"/> and
        /// connects to capture driver 0 (the first/default camera). Throws on
        /// failure — the caller (QrScanDialog) catches this and switches to
        /// the file-based fallback.
        /// </summary>
        public void Start(Control host)
        {
            _hwnd = capCreateCaptureWindowA("WgSharp QR scan", WS_CHILD | WS_VISIBLE,
                0, 0, host.ClientSize.Width, host.ClientSize.Height, host.Handle, 0);
            if (_hwnd == IntPtr.Zero)
                throw new Exception("Could not create a capture window (capCreateCaptureWindow failed).");

            if (SendMessage(_hwnd, WM_CAP_DRIVER_CONNECT, 0, 0) == 0)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                throw new Exception("No webcam driver responded to WM_CAP_DRIVER_CONNECT " +
                    "(no camera, or this camera's driver doesn't support the legacy capture API).");
            }
            _connected = true;

            SendMessage(_hwnd, WM_CAP_SET_SCALE, 1, 0);
            SendMessage(_hwnd, WM_CAP_SET_PREVIEWRATE, 30, 0);
            SendMessage(_hwnd, WM_CAP_SET_PREVIEW, 1, 0); // live preview renders directly into the host control
        }

        /// <summary>
        /// Grabs a still frame and returns it as a Bitmap, or null if the grab
        /// failed (transient — caller just tries again next tick) or nothing
        /// readable ended up on the clipboard.
        /// </summary>
        public Bitmap GrabFrame()
        {
            if (_hwnd == IntPtr.Zero || !_connected) return null;
            try
            {
                if (SendMessage(_hwnd, WM_CAP_GRAB_FRAME, 0, 0) == 0) return null;
                if (SendMessage(_hwnd, WM_CAP_COPY, 0, 0) == 0) return null;

                // The capture window put a DIB on the clipboard; read it back.
                // Done on the same (UI) thread the capture window lives on,
                // since clipboard access from a background thread is unreliable.
                IDataObject data = Clipboard.GetDataObject();
                if (data == null) return null;
                if (!data.GetDataPresent(DataFormats.Bitmap)) return null;
                Image img = data.GetData(DataFormats.Bitmap) as Image;
                if (img == null) return null;
                // Clone it: the Image returned by clipboard interop can be tied
                // to data that becomes invalid once the clipboard changes again.
                return new Bitmap(img);
            }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                if (_connected)
                {
                    try { SendMessage(_hwnd, WM_CAP_DRIVER_DISCONNECT, 0, 0); } catch { }
                    _connected = false;
                }
                try { DestroyWindow(_hwnd); } catch { }
                _hwnd = IntPtr.Zero;
            }
        }
    }
}

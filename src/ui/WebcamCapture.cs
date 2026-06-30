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
    /// interop layer to hand-write. avicap32 ships with Windows (no
    /// redistributable) and most UVC webcam drivers still register the
    /// compatibility shim it needs, but that's NOT guaranteed for every
    /// camera/driver — some newer or IR/Windows-Hello-only cameras don't
    /// expose it. QrScanDialog treats "no driver found" as a normal,
    /// non-fatal outcome and falls back to "scan from an image file".
    ///
    /// FRAMES: we register a frame callback (capSetCallbackOnFrame) and run a
    /// preview-rate capture stream. Each frame the driver produces is handed
    /// to our callback as a raw DIB (BITMAPINFOHEADER + pixel bytes); we
    /// convert the most recent one to a managed Bitmap on demand via
    /// GrabFrame(). This replaces an earlier, worse approach that grabbed
    /// single frames by copying them through the SYSTEM CLIPBOARD — which
    /// clobbered whatever the user had copied, on every frame. The callback
    /// path is the correct VFW idiom: it touches nothing global, and it
    /// doesn't depend on VFW's built-in preview window rendering correctly
    /// into a child control (which is unreliable), since QrScanDialog draws
    /// the preview itself from the same frames it decodes.
    ///
    /// KNOWN FAILURE MODE: on Windows 10/11, desktop (Win32) app camera
    /// access can be blocked system-wide by Settings -> Privacy & security ->
    /// Camera ("Camera access" / "Let desktop apps access your camera"),
    /// separate from any per-app prompt. When that's off, the modern Frame
    /// Server underneath this legacy API often lets the connect succeed
    /// (Start() doesn't throw; the LED may light) but delivers only black
    /// frames — or no frames at all. QrScanDialog detects both (no frames, or
    /// several consecutive blank frames) and points the user at that setting.
    /// </summary>
    public sealed class WebcamCapture : IDisposable
    {
        private const int WM_CAP_START = 0x400;
        private const int WM_CAP_DRIVER_CONNECT = WM_CAP_START + 10;
        private const int WM_CAP_DRIVER_DISCONNECT = WM_CAP_START + 11;
        private const int WM_CAP_SET_CALLBACK_FRAME = WM_CAP_START + 5;
        private const int WM_CAP_SET_PREVIEW = WM_CAP_START + 50;
        private const int WM_CAP_SET_PREVIEWRATE = WM_CAP_START + 52;
        private const int WM_CAP_SET_SCALE = WM_CAP_START + 53;
        private const int WM_CAP_SET_VIDEOFORMAT = WM_CAP_START + 45;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;

        [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
        private static extern IntPtr capCreateCaptureWindowA(string lpszWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, int nID);

        [DllImport("avicap32.dll")]
        private static extern bool capGetDriverDescriptionA(short wDriverIndex,
            byte[] lpszName, int cbName, byte[] lpszVer, int cbVer);

        [DllImport("user32.dll")]
        private static extern int SendMessage(IntPtr hWnd, int wMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hWnd);

        // The frame callback. lpVHdr points to a VIDEOHDR whose lpData is the
        // raw frame bytes (a bottom-up DIB body, format described by the
        // stream's BITMAPINFOHEADER, which we query once after connecting).
        private delegate void CapVideoCallback(IntPtr hWnd, IntPtr lpVHdr);

        [StructLayout(LayoutKind.Sequential)]
        private struct VIDEOHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesUsed;
            public int dwTimeCaptured;
            public IntPtr dwUser;
            public int dwFlags;
            public IntPtr dwReserved0;
            public IntPtr dwReserved1;
            public IntPtr dwReserved2;
            public IntPtr dwReserved3;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        private IntPtr _hwnd;
        private bool _connected;
        private CapVideoCallback _callback; // kept alive for the duration (prevents GC of the delegate)

        /// <summary>
        /// Optional diagnostic sink. QrScanDialog hooks this to forward camera
        /// internals into the app's Log tab. These lines are deliberately NOT
        /// debug-marked: camera capture on Windows is fragile enough (and hard
        /// enough to reproduce on a given machine) that the stage-by-stage
        /// trace is worth showing unconditionally, so a user reporting "the
        /// camera doesn't work" can read back exactly where it stopped without
        /// first having to discover and enable the Debug log toggle. Capture
        /// is short-lived and low-volume (a handful of lines per scan session),
        /// so this doesn't meaningfully add noise.
        /// </summary>
        public event Action<string> Log;
        private void L(string m)
        {
            var h = Log;
            if (h != null) h(WgSharp.Core.Logger.Tag(m, "Camera"));
        }

        private readonly object _frameLock = new object();
        private byte[] _latestFrame;   // most recent raw pixel bytes (copied out of the callback buffer)
        private int _frameWidth;
        private int _frameHeight;
        private int _frameBitCount;
        private long _frameSeq;        // increments each delivered frame; lets the caller tell "got any frames yet"
        private volatile bool _firstFramePending;

        /// <summary>True if at least one capture driver is registered.</summary>
        public static bool AnyDriverAvailable()
        {
            try
            {
                byte[] name = new byte[256];
                byte[] ver = new byte[256];
                for (short i = 0; i < 10; i++)
                    if (capGetDriverDescriptionA(i, name, name.Length, ver, ver.Length))
                        return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Logs every capture driver index Windows reports (name + version),
        /// for diagnostics. Instance method so it can use the Log event; safe
        /// to call before Start(). Returns the count found.
        /// </summary>
        public int LogAvailableDrivers()
        {
            int count = 0;
            try
            {
                byte[] name = new byte[256];
                byte[] ver = new byte[256];
                for (short i = 0; i < 10; i++)
                {
                    Array.Clear(name, 0, name.Length);
                    Array.Clear(ver, 0, ver.Length);
                    if (capGetDriverDescriptionA(i, name, name.Length, ver, ver.Length))
                    {
                        count++;
                        string n = System.Text.Encoding.ASCII.GetString(name).TrimEnd('\0', ' ');
                        string v = System.Text.Encoding.ASCII.GetString(ver).TrimEnd('\0', ' ');
                        L("Capture driver [" + i + "]: \"" + n + "\" (" + v + ")");
                    }
                }
                if (count == 0) L("No capture drivers reported by avicap32 (capGetDriverDescription found none).");
                else L(count + " capture driver(s) reported by avicap32.");
            }
            catch (Exception ex) { L("Driver enumeration threw: " + ex.Message); }
            return count;
        }

        /// <summary>Number of frames delivered so far. 0 well after Start() implies frames aren't flowing.</summary>
        public long FrameCount { get { lock (_frameLock) return _frameSeq; } }

        public void Start(Control host)
        {
            L("Creating capture window (host " + host.ClientSize.Width + "x" + host.ClientSize.Height + ").");
            _hwnd = capCreateCaptureWindowA("WgSharp QR scan", WS_CHILD | WS_VISIBLE,
                0, 0, host.ClientSize.Width, host.ClientSize.Height, host.Handle, 0);
            if (_hwnd == IntPtr.Zero)
            {
                L("capCreateCaptureWindow returned NULL.");
                throw new Exception("Could not create a capture window (capCreateCaptureWindow failed).");
            }
            L("Capture window created; sending WM_CAP_DRIVER_CONNECT.");

            if (SendMessage(_hwnd, WM_CAP_DRIVER_CONNECT, IntPtr.Zero, IntPtr.Zero) == 0)
            {
                L("WM_CAP_DRIVER_CONNECT failed (driver 0 did not connect).");
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
                throw new Exception("No webcam driver responded to WM_CAP_DRIVER_CONNECT " +
                    "(no camera, or this camera's driver doesn't support the legacy capture API).");
            }
            _connected = true;
            L("Driver connected.");

            // Log the negotiated format up front — this is the single most
            // useful diagnostic: if width/height/bit-depth look sane, the
            // pipeline is fundamentally working and any black image is a
            // privacy-block / content issue; if this fails, the driver
            // connected but won't describe its format, a different problem.
            int fw, fh, fbits;
            if (GetFrameFormat(out fw, out fh, out fbits))
                L("Negotiated video format: " + fw + "x" + fh + " @ " + fbits + " bpp.");
            else
                L("Could not read video format (WM_CAP_GET_VIDEOFORMAT failed).");

            // Register the frame callback BEFORE enabling preview, so every
            // frame the driver produces is delivered to us.
            _callback = OnFrame;
            IntPtr cbPtr = Marshal.GetFunctionPointerForDelegate(_callback);
            int cbOk = SendMessage(_hwnd, WM_CAP_SET_CALLBACK_FRAME, IntPtr.Zero, cbPtr);
            L("WM_CAP_SET_CALLBACK_FRAME returned " + cbOk + " (nonzero = ok).");

            // Enable VFW's own preview too (harmless if it works; we don't rely
            // on it). The reliable image path is the callback + our own paint.
            SendMessage(_hwnd, WM_CAP_SET_PREVIEWRATE, (IntPtr)33, IntPtr.Zero); // ~30 fps
            SendMessage(_hwnd, WM_CAP_SET_SCALE, (IntPtr)1, IntPtr.Zero);
            int prevOk = SendMessage(_hwnd, WM_CAP_SET_PREVIEW, (IntPtr)1, IntPtr.Zero);
            L("WM_CAP_SET_PREVIEW returned " + prevOk + ". Waiting for frames\u2026");
        }

        private void OnFrame(IntPtr hWnd, IntPtr lpVHdr)
        {
            try
            {
                if (lpVHdr == IntPtr.Zero) return;
                VIDEOHDR hdr = (VIDEOHDR)Marshal.PtrToStructure(lpVHdr, typeof(VIDEOHDR));
                if (hdr.lpData == IntPtr.Zero || hdr.dwBytesUsed <= 0) return;

                // Resolve the frame geometry from the stream's current format.
                // Queried lazily on the first frame (and re-checked cheaply) so
                // we know width/height/bit-depth for the raw pixel bytes.
                int w, h, bits;
                if (!GetFrameFormat(out w, out h, out bits)) return;
                if (w <= 0 || h <= 0 || (bits != 24 && bits != 32)) return;

                byte[] buf = new byte[hdr.dwBytesUsed];
                Marshal.Copy(hdr.lpData, buf, 0, hdr.dwBytesUsed);

                lock (_frameLock)
                {
                    _latestFrame = buf;
                    _frameWidth = w;
                    _frameHeight = h;
                    _frameBitCount = bits;
                    _frameSeq++;
                    if (_frameSeq == 1)
                        _firstFramePending = true; // log outside the lock below
                }
                if (_firstFramePending)
                {
                    _firstFramePending = false;
                    L("First frame delivered: " + w + "x" + h + " @ " + bits + " bpp, " +
                      hdr.dwBytesUsed + " bytes.");
                }
            }
            catch { /* never let an exception cross back into native capture code */ }
        }

        private bool GetFrameFormat(out int width, out int height, out int bitCount)
        {
            width = height = bitCount = 0;
            // WM_CAP_GET_VIDEOFORMAT (= WM_CAP_START + 44): wParam = buffer size,
            // lParam = buffer; returns the size and fills a BITMAPINFO. We only
            // need the header.
            const int WM_CAP_GET_VIDEOFORMAT = WM_CAP_START + 44;
            int size = Marshal.SizeOf(typeof(BITMAPINFOHEADER)) + 256 * 4; // header + room for a palette
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int got = SendMessage(_hwnd, WM_CAP_GET_VIDEOFORMAT, (IntPtr)size, buf);
                if (got == 0) return false;
                BITMAPINFOHEADER bih = (BITMAPINFOHEADER)Marshal.PtrToStructure(buf, typeof(BITMAPINFOHEADER));
                width = bih.biWidth;
                height = Math.Abs(bih.biHeight);
                bitCount = bih.biBitCount;
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>
        /// Returns the most recently captured frame as a managed Bitmap, or
        /// null if no frame has arrived yet (or it couldn't be decoded).
        ///
        /// Two genuinely different cases land here, and we can't know in
        /// advance which one a given driver delivers:
        ///   - RAW, uncompressed BGR pixel data matching the negotiated
        ///     BITMAPINFOHEADER exactly (byte count == stride * height). The
        ///     DIB body is bottom-up, so we flip it to top-down while
        ///     building the Bitmap.
        ///   - COMPRESSED data — in practice this means MJPEG, which is by
        ///     far the most common default streaming format for UVC webcams
        ///     at anything above a low resolution. Each MJPEG frame IS a
        ///     complete, standalone JPEG image, so GDI+'s own JPEG decoder
        ///     (via the Bitmap(Stream) constructor) handles it directly —
        ///     no need to hand-write a JPEG decoder. The byte count for a
        ///     compressed frame is naturally much smaller than the raw
        ///     stride*height size (a 1280x720 frame can compress from
        ///     ~2.7 MB raw down to under 150 KB), which is exactly the
        ///     mismatch that distinguishes the two cases here: if it
        ///     doesn't match the raw size, try decoding it as a standard
        ///     image rather than just rejecting it.
        public Bitmap GrabFrame()
        {
            byte[] buf; int w, h, bits;
            lock (_frameLock)
            {
                if (_latestFrame == null) return null;
                buf = _latestFrame;
                w = _frameWidth; h = _frameHeight; bits = _frameBitCount;
            }

            int bytesPerPixel = bits / 8;
            int srcStride = ((w * bytesPerPixel + 3) / 4) * 4; // DIB rows are DWORD-aligned
            bool looksRaw = bytesPerPixel > 0 && buf.Length >= srcStride * h;

            if (!looksRaw)
            {
                // Likely MJPEG (or some other compressed format GDI+'s
                // decoder recognizes from the bytes themselves). Let GDI+
                // sniff and decode it directly; only genuine garbage throws.
                try
                {
                    using (var ms = new System.IO.MemoryStream(buf))
                    using (var decoded = new Bitmap(ms))
                    {
                        // Clone: the Bitmap(Stream) ctor keeps the stream
                        // open and tied to the image's lifetime, but `buf`
                        // (and the MemoryStream wrapping it) is about to go
                        // out of scope — clone to a standalone Bitmap first.
                        return new Bitmap(decoded);
                    }
                }
                catch { return null; } // not a format GDI+ recognizes either; a genuinely bad frame
            }

            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[bd.Stride];
                    for (int y = 0; y < h; y++)
                    {
                        // bottom-up source: row 0 of the DIB is the bottom of the image
                        int srcRow = (h - 1 - y) * srcStride;
                        for (int x = 0; x < w; x++)
                        {
                            int sp = srcRow + x * bytesPerPixel;
                            int dp = x * 3;
                            // DIB pixels are BGR(A); destination is also BGR order.
                            row[dp] = buf[sp];
                            row[dp + 1] = buf[sp + 1];
                            row[dp + 2] = buf[sp + 2];
                        }
                        Marshal.Copy(row, 0, (IntPtr)(bd.Scan0.ToInt64() + y * bd.Stride), bd.Stride);
                    }
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            if (_hwnd != IntPtr.Zero)
            {
                try { SendMessage(_hwnd, WM_CAP_SET_CALLBACK_FRAME, IntPtr.Zero, IntPtr.Zero); } catch { }
                if (_connected)
                {
                    try { SendMessage(_hwnd, WM_CAP_DRIVER_DISCONNECT, IntPtr.Zero, IntPtr.Zero); } catch { }
                    _connected = false;
                }
                try { DestroyWindow(_hwnd); } catch { }
                _hwnd = IntPtr.Zero;
            }
            _callback = null;
            lock (_frameLock) { _latestFrame = null; }
        }
    }
}

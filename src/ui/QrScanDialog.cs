using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Lets the user add a tunnel by scanning a QR code with the webcam (the
    /// same QR a tunnel's own "Show QR" produces, or one from the official
    /// WireGuard mobile app). On success, <see cref="DecodedText"/> holds the
    /// plain .conf text and ShowDialog returns DialogResult.OK.
    ///
    /// Webcam capture uses the legacy VFW API (see WebcamCapture) and is
    /// genuinely best-effort — some cameras/drivers don't expose it. If no
    /// capture driver is found at all, or the live attempt fails, this falls
    /// back to letting the user pick an image file (a screenshot or photo of
    /// the QR) and decodes that instead, so the feature still works either way.
    /// </summary>
    public sealed class QrScanDialog : Form
    {
        public string DecodedText { get; private set; }

        /// <summary>
        /// Optional diagnostic sink. MainForm hooks this so camera internals
        /// and scan-dialog state transitions land in the app's Log tab (gated
        /// behind Debug log). Lines arrive already debug-marked and tagged.
        /// </summary>
        public event Action<string> Log;
        private void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Camera")); }

        private readonly Panel _previewHost;
        private readonly PictureBox _preview;
        private readonly Label _status;
        private readonly Button _btnFile;
        private readonly Button _btnPrivacy;
        private readonly Button _btnCancel;
        private readonly Timer _timer;

        private WebcamCapture _cam;
        private bool _busy; // re-entrancy guard: skip a tick if the previous decode attempt is still running

        // How many consecutive grabbed frames came back essentially blank
        // (see IsLikelyBlank). The live preview rendering INTO _previewHost is
        // drawn directly by the OS/driver — we never see those pixels — so
        // this is judged from the stills GrabFrame already pulls every tick
        // for decoding, not from the preview itself. A handful of blank
        // frames in a row (not just one — the very first frame or two can
        // legitimately be black while the sensor warms up) is the strong,
        // specific signature of Windows' camera privacy permission blocking
        // desktop-app camera access: the legacy capture API connects
        // successfully (so Start() doesn't throw and the LED lights up), but
        // the modern Frame Server sitting underneath withholds the actual
        // image data instead of failing the call outright.
        private int _consecutiveBlankFrames;
        // The timer now ticks ~every 150ms (fast, for a smooth preview). All
        // thresholds below are in ticks at that interval.
        private const int BlankFrameThreshold = 16;  // ~2.4s of solid-black frames before the privacy hint
        private const int NoFrameTicksThreshold = 20; // ~3s with ZERO frames delivered -> same hint
        private const int DecodeEveryTicks = 3;        // attempt a decode ~every 450ms; paint every tick
        private bool _privacyHintShown;
        private long _ticks;
        private long _lastDecodeTick;

        public QrScanDialog()
        {
            Text = "Scan from QR code";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(420, 462);

            _previewHost = new Panel
            {
                Location = new Point(16, 16),
                Size = new Size(388, 320),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };

            // We draw the live preview ourselves into this PictureBox from the
            // frames the capture callback delivers, rather than relying on
            // VFW's own preview rendering into the host panel (which is
            // unreliable across cameras/drivers). The PictureBox fills the
            // host panel and sits ON TOP of wherever VFW might also be drawing.
            _preview = new PictureBox
            {
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Black
            };
            _previewHost.Controls.Add(_preview);

            _status = new Label
            {
                Text = "Starting the camera\u2026",
                Location = new Point(16, 344),
                Size = new Size(388, 50),
                ForeColor = Color.FromArgb(0x44, 0x44, 0x44)
            };

            // Hidden until a likely permission block is detected (see
            // OnTimerTick / IsLikelyBlank) — opens the exact Windows Settings
            // page for camera access, since that's overwhelmingly the actual
            // fix when the picture stays black despite the camera "turning on".
            _btnPrivacy = new Button
            {
                Text = "Open camera privacy settings\u2026",
                Location = new Point(16, 396),
                Size = new Size(220, 28),
                Visible = false
            };
            _btnPrivacy.Click += OnOpenPrivacySettings;

            _btnFile = new Button
            {
                Text = "Scan from image file\u2026",
                Location = new Point(16, 428),
                Size = new Size(180, 28)
            };
            _btnFile.Click += OnScanFromFile;

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(316, 428),
                Size = new Size(88, 28)
            };

            Controls.Add(_previewHost);
            Controls.Add(_status);
            Controls.Add(_btnPrivacy);
            Controls.Add(_btnFile);
            Controls.Add(_btnCancel);
            CancelButton = _btnCancel;

            _timer = new Timer { Interval = 150 };
            _timer.Tick += OnTimerTick;

            Load += OnLoad;
            FormClosed += delegate { StopCamera(); };
        }

        private void OnLoad(object sender, EventArgs e)
        {
            // This first line is intentionally NOT debug-marked, so it shows
            // even with Debug log OFF. It's a definitive probe: if you open
            // the scanner and don't see this line in the Log tab at all, the
            // running binary predates this logging (rebuild from source), or
            // the dialog's Log event isn't wired — NOT a camera problem. Every
            // line after this one IS debug-marked (via WebcamCapture and the
            // _cam.Log forward), so those need Debug log ON to appear.
            L("QR scanner opened (this line shows regardless of Debug log).");
            if (!WebcamCapture.AnyDriverAvailable())
            {
                L("AnyDriverAvailable() = false; no legacy capture driver present.");
                _status.Text = "No webcam was found (or this camera doesn't support the legacy " +
                    "capture API WgSharp uses). You can still scan from a saved image instead.";
                return;
            }

            try
            {
                _cam = new WebcamCapture();
                _cam.Log += L;            // forward camera internals to the app log
                _cam.LogAvailableDrivers(); // enumerate what Windows reports, for diagnostics
                _cam.Start(_previewHost);
                _status.Text = "Point the camera at the QR code\u2026";
                _timer.Start();
            }
            catch (Exception ex)
            {
                L("Camera Start() threw: " + ex.Message);
                _cam = null;
                _status.Text = "Couldn't start the webcam (" + ex.Message + "). " +
                    "You can still scan from a saved image instead.";
            }
        }

        private void OnTimerTick(object sender, EventArgs e)
        {
            if (_busy || _cam == null) return;
            _busy = true;
            try
            {
                // No frames arriving at all (distinct from blank frames): the
                // capture connected but the driver/Frame Server is delivering
                // nothing. After a short grace period this is the SAME
                // permission story as all-black frames, so steer to the same
                // fix rather than leaving a silent black box.
                _ticks++;
                if (_cam.FrameCount == 0)
                {
                    if (_ticks >= NoFrameTicksThreshold && !_privacyHintShown)
                    {
                        _privacyHintShown = true;
                        L("No frames delivered after " + _ticks + " ticks; showing privacy hint.");
                        _status.Text = "The camera connected but isn't delivering any video. On Windows " +
                            "this is usually the camera privacy setting blocking desktop apps \u2014 click below to check.";
                        _btnPrivacy.Visible = true;
                    }
                    return;
                }

                Bitmap frame = _cam.GrabFrame();
                if (frame == null) return;

                // Always paint the latest frame as the live preview, replacing
                // (and disposing) the previous one. This is what the user sees
                // moving, independent of whether we attempt a decode this tick.
                Image old = _preview.Image;
                _preview.Image = (Bitmap)frame.Clone();
                if (old != null) old.Dispose();

                bool blank = IsLikelyBlank(frame);
                if (blank)
                {
                    _consecutiveBlankFrames++;
                    if (_consecutiveBlankFrames == BlankFrameThreshold && !_privacyHintShown)
                    {
                        _privacyHintShown = true;
                        L("Frames arriving but consistently blank (" + _consecutiveBlankFrames +
                          " in a row); showing privacy hint.");
                        _status.Text = "The picture is staying black even though the camera connected. " +
                            "This usually means Windows is blocking desktop apps from using the camera \u2014 " +
                            "click below to check.";
                        _btnPrivacy.Visible = true;
                    }
                }
                else
                {
                    // A real (non-blank) frame: clear any prior warning state.
                    if (_consecutiveBlankFrames > 0 || _btnPrivacy.Visible)
                    {
                        _consecutiveBlankFrames = 0;
                        _privacyHintShown = false;
                        _btnPrivacy.Visible = false;
                        _status.Text = "Point the camera at the QR code\u2026";
                    }
                }

                // Decode is more expensive than a paint, so don't run it on
                // every fast preview tick — roughly 2-3 times a second is
                // plenty for a QR held up to the camera, and keeps the preview
                // smooth. Skip it on a blank frame (nothing to find).
                if (!blank && (_ticks - _lastDecodeTick) >= DecodeEveryTicks)
                {
                    _lastDecodeTick = _ticks;
                    string text = QrImageLocator.TryDecodeFrame(frame);
                    if (text != null)
                    {
                        L("QR decoded from a webcam frame (" + text.Length + " chars).");
                        DecodedText = text;
                        _timer.Stop();
                        frame.Dispose();
                        DialogResult = DialogResult.OK;
                        Close();
                        return;
                    }
                }

                frame.Dispose();
            }
            catch { /* a single bad frame is not fatal; just try again next tick */ }
            finally { _busy = false; }
        }

        /// <summary>
        /// True if a captured frame is essentially a single flat color (near-
        /// black, but also catches a uniform gray/green "no signal" frame
        /// some drivers substitute). Samples a small grid of pixels rather
        /// than every pixel — this only needs to be a cheap, reliable signal,
        /// not a precise measurement, and runs every 400ms.
        /// </summary>
        private static bool IsLikelyBlank(Bitmap bmp)
        {
            const int gridSize = 12;
            int w = bmp.Width, h = bmp.Height;
            if (w < gridSize || h < gridSize) return false;

            long sum = 0, sumSq = 0;
            int n = 0;
            for (int gy = 0; gy < gridSize; gy++)
            {
                int y = h * gy / gridSize;
                for (int gx = 0; gx < gridSize; gx++)
                {
                    int x = w * gx / gridSize;
                    Color c = bmp.GetPixel(x, y);
                    int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                    sum += lum;
                    sumSq += (long)lum * lum;
                    n++;
                }
            }
            double mean = (double)sum / n;
            double variance = (double)sumSq / n - mean * mean;
            // Near-black (low average brightness) OR perfectly flat (near-zero
            // variance, e.g. a uniform placeholder frame) — a real QR-bearing
            // scene has both reasonable brightness AND real contrast.
            return mean < 8.0 || variance < 4.0;
        }

        private void OnOpenPrivacySettings(object sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:privacy-webcam",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "Couldn't open Settings automatically (" + ex.Message + ").\n\n" +
                    "Open it manually: Settings \u2192 Privacy & security \u2192 Camera, and make sure " +
                    "\"Camera access\" and \"Let desktop apps access your camera\" are both turned on.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void OnScanFromFile(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Select an image of a QR code",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*"
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (Bitmap img = new Bitmap(ofd.FileName))
                    {
                        string diagnostic;
                        string text = QrImageLocator.TryDecodeFrame(img, out diagnostic);
                        if (text == null)
                        {
                            MessageBox.Show(this, "Couldn't find a readable QR code in that image.\n\n" +
                                (diagnostic ?? "No further detail available."),
                                "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        DecodedText = text;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Couldn't read that image: " + ex.Message, "WgSharp",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void StopCamera()
        {
            _timer.Stop();
            if (_cam != null) { _cam.Dispose(); _cam = null; }
            if (_preview != null && _preview.Image != null)
            {
                Image img = _preview.Image;
                _preview.Image = null;
                img.Dispose();
            }
        }
    }
}

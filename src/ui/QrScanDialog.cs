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

        private readonly Panel _previewHost;
        private readonly Label _status;
        private readonly Button _btnFile;
        private readonly Button _btnCancel;
        private readonly Timer _timer;

        private WebcamCapture _cam;
        private bool _busy; // re-entrancy guard: skip a tick if the previous decode attempt is still running

        public QrScanDialog()
        {
            Text = "Scan from QR code";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(420, 430);

            _previewHost = new Panel
            {
                Location = new Point(16, 16),
                Size = new Size(388, 320),
                BackColor = Color.Black,
                BorderStyle = BorderStyle.FixedSingle
            };

            _status = new Label
            {
                Text = "Starting the camera\u2026",
                Location = new Point(16, 344),
                Size = new Size(388, 36),
                ForeColor = Color.FromArgb(0x44, 0x44, 0x44)
            };

            _btnFile = new Button
            {
                Text = "Scan from image file\u2026",
                Location = new Point(16, 388),
                Size = new Size(180, 28)
            };
            _btnFile.Click += OnScanFromFile;

            _btnCancel = new Button
            {
                Text = "Cancel",
                DialogResult = DialogResult.Cancel,
                Location = new Point(316, 388),
                Size = new Size(88, 28)
            };

            Controls.Add(_previewHost);
            Controls.Add(_status);
            Controls.Add(_btnFile);
            Controls.Add(_btnCancel);
            CancelButton = _btnCancel;

            _timer = new Timer { Interval = 400 };
            _timer.Tick += OnTimerTick;

            Load += OnLoad;
            FormClosed += delegate { StopCamera(); };
        }

        private void OnLoad(object sender, EventArgs e)
        {
            if (!WebcamCapture.AnyDriverAvailable())
            {
                _status.Text = "No webcam was found (or this camera doesn't support the legacy " +
                    "capture API WgSharp uses). You can still scan from a saved image instead.";
                return;
            }

            try
            {
                _cam = new WebcamCapture();
                _cam.Start(_previewHost);
                _status.Text = "Point the camera at the QR code\u2026";
                _timer.Start();
            }
            catch (Exception ex)
            {
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
                using (Bitmap frame = _cam.GrabFrame())
                {
                    if (frame == null) return;
                    string text = QrImageLocator.TryDecodeFrame(frame);
                    if (text != null)
                    {
                        DecodedText = text;
                        _timer.Stop();
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                }
            }
            catch { /* a single bad frame is not fatal; just try again next tick */ }
            finally { _busy = false; }
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
        }
    }
}

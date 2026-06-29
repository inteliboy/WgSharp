using System;
using System.Drawing;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Shows a QR code of the tunnel configuration, scannable by the WireGuard
    /// mobile app. Includes a Save button to write the QR as a PNG.
    /// </summary>
    public sealed class QrDialog : Form
    {
        private readonly bool[,] _modules;
        private readonly string _name;

        public QrDialog(string name, string configText)
        {
            _name = name;

            // The QR encodes the raw config exactly like the standard qrencode
            // workflow. The official WireGuard mobile app does not read a name from
            // the QR content -- it prompts the user to type a tunnel name on scan --
            // so we don't embed one (it would just bloat the code).
            _modules = QrCode.Encode(configText, QrCode.Ecc.M);

            Text = "QR code \u2014 " + name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(360, 436);
            BackColor = Color.White;

            var pic = new PictureBox
            {
                Location = new Point(20, 20),
                Size = new Size(320, 320),
                SizeMode = PictureBoxSizeMode.Zoom,
                Image = Render(320),
                BackColor = Color.White
            };

            var hint = new Label
            {
                Text = "Scan with the WireGuard app, then name the tunnel on your phone.",
                Location = new Point(20, 346),
                Size = new Size(320, 40),     // two lines so the full sentence shows
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.FromArgb(0x55, 0x55, 0x55)
            };

            var btnSave = new Button
            {
                Text = "Save PNG\u2026",
                Location = new Point(20, 394),
                Size = new Size(100, 28)
            };
            btnSave.Click += OnSave;

            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Location = new Point(240, 394),
                Size = new Size(100, 28)
            };

            Controls.Add(pic);
            Controls.Add(hint);
            Controls.Add(btnSave);
            Controls.Add(btnClose);
            AcceptButton = btnClose;
            CancelButton = btnClose;
        }

        private Bitmap Render(int pixels)
        {
            int n = _modules.GetLength(0);
            int quiet = 4; // quiet zone in modules
            int total = n + quiet * 2;
            int scale = System.Math.Max(1, pixels / total);
            int imgSize = scale * total;

            var bmp = new Bitmap(imgSize, imgSize);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using (var black = new SolidBrush(Color.Black))
                    for (int r = 0; r < n; r++)
                        for (int c = 0; c < n; c++)
                            if (_modules[r, c])
                                g.FillRectangle(black,
                                    (c + quiet) * scale, (r + quiet) * scale, scale, scale);
            }
            return bmp;
        }

        private void OnSave(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Filter = "PNG image (*.png)|*.png";
                dlg.FileName = _name + ".png";
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    using (var img = Render(1024))
                        img.Save(dlg.FileName, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WgSharp.Ui
{
    /// <summary>
    /// A small About dialog reached from the window's system menu
    /// ("About WgSharp..."). Shows the app icon (like the original client),
    /// version/architecture/OS/driver info, and brief credit to the original
    /// WireGuard/Wintun/WireGuardNT author and their licenses.
    /// </summary>
    public sealed class AboutDialog : Form
    {
        public AboutDialog()
        {
            Text = "About WgSharp";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ClientSize = new Size(460, 420);
            Font = new Font("Segoe UI", 9F);

            // ---- icon, like the original client's About dialog ----
            var iconBox = new PictureBox
            {
                Location = new Point(20, 18),
                Size = new Size(64, 64),
                SizeMode = PictureBoxSizeMode.StretchImage
            };
            try
            {
                // Read the largest frame directly out of the exe's own
                // embedded icon resource and decode it as PNG — see
                // AppIconLoader for why System.Drawing.Icon isn't used here
                // (it's what was producing the garbled "white noise" look).
                Bitmap bmp = AppIconLoader.LoadLargestEmbeddedIcon();
                if (bmp != null)
                {
                    iconBox.Image = bmp;
                }
                else
                {
                    // Last-resort fallback: small extracted icon, stretched.
                    // Soft-looking, but never garbled.
                    using (Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                        if (ico != null) iconBox.Image = ico.ToBitmap();
                }
            }
            catch { }

            var title = new Label
            {
                Text = "WgSharp",
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                Location = new Point(96, 18),
                Size = new Size(340, 28),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var subtitle = new Label
            {
                Text = "An independent, from-scratch WireGuard client for Windows.",
                Location = new Point(96, 48),
                Size = new Size(340, 32),
                ForeColor = Color.FromArgb(0x44, 0x44, 0x44)
            };

            // ---- version / architecture / OS / driver info ----
            var infoBox = new Label
            {
                Location = new Point(20, 92),
                Size = new Size(420, 110),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = Color.FromArgb(0x33, 0x33, 0x33),
                Text =
                    "Version: " + GetBuildVersion() + "\n" +
                    "Architecture: " + WgSharp.Tun.WintunDownloader.ArchFolder() + "\n" +
                    "wintun.dll: " + GetDllVersion("wintun.dll") + "\n" +
                    "wireguard.dll: " + GetDllVersion("wireguard.dll") + "\n" +
                    "Operating system: " + GetOsDescription()
            };

            var credits = new Label
            {
                Location = new Point(20, 210),
                Size = new Size(420, 90),
                Text =
                    "Uses the WireGuard protocol and the Wintun / WireGuardNT drivers,\n" +
                    "created by Jason A. Donenfeld. Their source is licensed under the\n" +
                    "GNU GPLv2; the prebuilt driver DLLs are under WireGuard LLC's own\n" +
                    "Prebuilt Binaries License. WgSharp's own code is MIT licensed.",
                ForeColor = Color.FromArgb(0x33, 0x33, 0x33)
            };

            var copyright = new Label
            {
                Text = "Copyright \u00A9 2026 inteliboy",
                Location = new Point(20, 308),
                AutoSize = true,
                ForeColor = Color.FromArgb(0x55, 0x55, 0x55)
            };

            // Measure the copyright text so we can place the GitHub link
            // exactly one space after it on the same line, regardless of
            // the user's font scaling or DPI settings.
            int copyrightW = TextRenderer.MeasureText(
                copyright.Text + " ", new Font("Segoe UI", 9F)).Width;

            var link = new LinkLabel
            {
                Text = "github.com/inteliboy/WgSharp",
                Location = new Point(20 + copyrightW, 308),
                AutoSize = true
            };
            link.LinkClicked += delegate
            {
                try { Process.Start("https://github.com/inteliboy/WgSharp"); } catch { }
            };

            int coffeeLabelW = TextRenderer.MeasureText(
                "\u2665 Support this project: ", new Font("Segoe UI", 9F)).Width;

            var coffeeLabel = new Label
            {
                Text = "\u2665 Support this project:",
                Location = new Point(20, 330),
                AutoSize = true,
                ForeColor = Color.FromArgb(0x55, 0x55, 0x55)
            };

            var coffeeLink = new LinkLabel
            {
                Text = "buymeacoffee.com/inteliboy",
                Location = new Point(20 + coffeeLabelW, 330),
                AutoSize = true
            };
            coffeeLink.LinkClicked += delegate
            {
                try { Process.Start("https://buymeacoffee.com/inteliboy"); } catch { }
            };

            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = new Size(88, 28),
                Location = new Point(ClientSize.Width - 108, ClientSize.Height - 40)
            };

            var btnCheckUpdates = new Button
            {
                Text = "Check for Updates",
                Size = new Size(140, 28),
                Location = new Point(20, ClientSize.Height - 40)
            };
            btnCheckUpdates.Click += delegate { CheckForUpdatesInteractive(btnCheckUpdates); };

            Controls.Add(iconBox);
            Controls.Add(title);
            Controls.Add(subtitle);
            Controls.Add(infoBox);
            Controls.Add(credits);
            Controls.Add(copyright);
            Controls.Add(link);
            Controls.Add(coffeeLabel);
            Controls.Add(coffeeLink);
            Controls.Add(btnCheckUpdates);
            Controls.Add(btnClose);

            AcceptButton = btnClose;
            CancelButton = btnClose;
        }

        /// <summary>
        /// Runs an update check triggered by the user (About dialog / tray menu),
        /// so unlike the silent startup check this always reports an outcome —
        /// up to date, a newer version (with an offer to open the page), or a
        /// check failure. Runs the network call on a background thread and
        /// re-enables the button + shows the result on the UI thread. Static so
        /// the tray menu can reuse it without an About dialog instance.
        /// </summary>
        public static void CheckForUpdatesInteractive(Button trigger)
        {
            IWin32Window owner = trigger != null ? trigger.FindForm() : null;
            if (trigger != null) { trigger.Enabled = false; trigger.Text = "Checking\u2026"; }

            WgSharp.Core.UpdateChecker.CheckAsync(delegate (WgSharp.Core.UpdateChecker.Result res)
            {
                Action show = delegate
                {
                    if (trigger != null && !trigger.IsDisposed) { trigger.Enabled = true; trigger.Text = "Check for Updates"; }
                    ShowUpdateResult(owner, res);
                };
                // Marshal to the UI thread if we have a control to marshal on.
                try
                {
                    Control c = trigger;
                    if (c != null && c.IsHandleCreated && c.InvokeRequired) c.BeginInvoke(show);
                    else show();
                }
                catch { try { show(); } catch { } }
            });
        }

        /// <summary>Shows the outcome of an interactive update check (public so the tray menu can reuse it).</summary>
        public static void ShowUpdateResult(IWin32Window owner, WgSharp.Core.UpdateChecker.Result res)
        {
            if (res == null)
            {
                MessageBox.Show(owner, "Couldn't check for updates right now. Please try again later.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (res.IsUpdateAvailable)
            {
                var answer = MessageBox.Show(owner,
                    "A newer version is available.\r\n\r\n" +
                    "You have " + res.CurrentVersion + "; the latest is " + res.LatestVersion + ".\r\n\r\n" +
                    "Open the download page now?",
                    "WgSharp \u2014 update available",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                {
                    try { Process.Start(res.ReleaseUrl); } catch { }
                }
                return;
            }

            // No update. Distinguish "confirmed up to date" from "couldn't
            // reach GitHub": LatestVersion is only set when the API answered.
            if (!string.IsNullOrEmpty(res.LatestVersion))
                MessageBox.Show(owner,
                    "You're up to date.\r\n\r\nWgSharp " + res.CurrentVersion + " is the latest version.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                MessageBox.Show(owner,
                    "Couldn't reach GitHub to check for updates. Please check your connection and try again.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Reads the exe's own embedded version: "1.YY.MMDD.0", stamped by
        /// build.cmd into AssemblyInformationalVersion at build time (see
        /// build.cmd and src/core/AssemblyInfo.generated.cs). The MSI
        /// installer (if built) uses the leading 3 fields of this same date
        /// encoding for its own ProductVersion, "1.YY.MMDD" -- Windows
        /// Installer's version field is strictly 3-part, so it can't carry
        /// the trailing ".0" the exe's 4-part assembly version fields use,
        /// but both are stamped from the exact same YY/MM/DD digits in one
        /// place in build.cmd.
        /// </summary>
        private static string GetBuildVersion()
        {
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    string v = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            return "unknown";
        }

        private static string GetDllVersion(string fileName)
        {
            try
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
                if (!File.Exists(path)) return "not present";
                var info = FileVersionInfo.GetVersionInfo(path);
                string v = info.FileVersion;
                return string.IsNullOrEmpty(v) ? "present (no version info)" : v;
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// "Windows (Build 26200.7589)" — deliberately NOT the SKU/edition name
        /// (e.g. "Windows 11 Pro" or "Windows 11 Home"). The edition doesn't
        /// affect anything WgSharp does, and Windows 10 vs. 11 is itself just a
        /// marketing line drawn at a build-number threshold, not a real
        /// distinction the app cares about (everything WgSharp depends on
        /// keys off the actual API/driver surface, which the build number
        /// already pins precisely). The build number is what actually
        /// distinguishes one Windows installation's capabilities from another.
        /// </summary>
        private static string GetOsDescription()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object buildNumber = key.GetValue("CurrentBuildNumber");
                        object ubr = key.GetValue("UBR"); // update build revision, e.g. 26100.4351
                        if (buildNumber != null)
                        {
                            string build = buildNumber.ToString();
                            if (ubr != null) build += "." + ubr;
                            return "Windows (Build " + build + ")";
                        }
                    }
                }
            }
            catch { }
            // Fall back to the always-available, less pretty version string.
            return Environment.OSVersion.VersionString;
        }
    }
}

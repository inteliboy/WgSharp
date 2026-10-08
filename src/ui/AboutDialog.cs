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
            AutoScaleMode = AutoScaleMode.None; // layout is scaled by hand via Dpi.S
            ClientSize = Dpi.Sz(460, 420);
            Font = new Font("Segoe UI", 9F);
            BackColor = AppTheme.WindowBg;
            HandleCreated += delegate
            {
                NativeMethods.SetDarkTitleBar(Handle, AppTheme.IsDark);
                NativeMethods.SetBorderAndCaptionColor(Handle,
                    AppTheme.IsDark ? AppTheme.Border : Color.Empty,
                    AppTheme.IsDark ? AppTheme.Surface : Color.Empty);
            };

            // ---- icon, like the original client's About dialog ----
            // SizeMode=Normal, not StretchImage: PictureBox's own StretchImage
            // draws with GDI+'s plain default settings, whose bilinear filter
            // isn't enough for a 4:1 reduction (256x256 source down to 64x64)
            // and leaves the circle/glyph edges visibly staircased even though
            // the source PNG itself is cleanly anti-aliased. Pre-scaling it
            // ourselves with HighQualityBicubic + AntiAlias below and handing
            // PictureBox the already-correctly-sized result avoids that.
            var iconBox = new PictureBox
            {
                Location = Dpi.Pt(20, 18),
                Size = Dpi.Sz(64, 64),
                SizeMode = PictureBoxSizeMode.Normal
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
                    iconBox.Image = HighQualityResize(bmp, iconBox.Width, iconBox.Height);
                    bmp.Dispose();
                }
                else
                {
                    // Last-resort fallback: small extracted icon, upscaled.
                    // Soft-looking, but never garbled.
                    using (Icon ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath))
                        if (ico != null)
                            using (Bitmap small = ico.ToBitmap())
                                iconBox.Image = HighQualityResize(small, iconBox.Width, iconBox.Height);
                }
            }
            catch { }

            var title = new Label
            {
                Text = "WgSharp",
                Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
                Location = Dpi.Pt(96, 18),
                Size = Dpi.Sz(340, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = AppTheme.FieldValue
            };

            var subtitle = new Label
            {
                Text = "An independent, from-scratch WireGuard client for Windows.",
                Location = Dpi.Pt(96, 48),
                Size = Dpi.Sz(340, 32),
                ForeColor = AppTheme.FieldLabel
            };

            // ---- version / architecture / OS / driver info ----
            var infoBox = new Label
            {
                Location = Dpi.Pt(20, 92),
                Size = Dpi.Sz(420, 110),
                Font = new Font("Segoe UI", 8.5F),
                ForeColor = AppTheme.FieldValue,
                Text =
                    "Version: " + GetBuildVersion() + "\n" +
                    "Architecture: " + WgSharp.Tun.WintunDownloader.ArchFolder() + "\n" +
                    "wintun.dll: " + GetDllVersion("wintun.dll") + "\n" +
                    "wireguard.dll: " + GetDllVersion("wireguard.dll") + "\n" +
                    "Operating system: " + GetOsDescription()
            };

            var credits = new Label
            {
                Location = Dpi.Pt(20, 210),
                Size = Dpi.Sz(420, 90),
                Text =
                    "Uses the WireGuard protocol and the Wintun / WireGuardNT drivers,\n" +
                    "created by Jason A. Donenfeld. Their source is licensed under the\n" +
                    "GNU GPLv2; the prebuilt driver DLLs are under WireGuard LLC's own\n" +
                    "Prebuilt Binaries License. WgSharp's own code is MIT licensed.",
                ForeColor = AppTheme.FieldValue
            };

            var copyright = new Label
            {
                Text = "Copyright \u00A9 2026 inteliboy",
                Location = Dpi.Pt(20, 308),
                AutoSize = true,
                ForeColor = AppTheme.FieldLabel
            };

            // Measure the copyright text so we can place the GitHub link
            // exactly one space after it on the same line, regardless of
            // the user's font scaling or DPI settings.
            int copyrightW = TextRenderer.MeasureText(
                copyright.Text + " ", new Font("Segoe UI", 9F)).Width;

            var link = new LinkLabel
            {
                Text = "github.com/inteliboy/WgSharp",
                Location = new Point(Dpi.S(20) + copyrightW, Dpi.S(308)),
                AutoSize = true,
                LinkColor = AppTheme.Accent,
                ActiveLinkColor = AppTheme.AccentHover,
                VisitedLinkColor = AppTheme.Accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline
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
                Location = Dpi.Pt(20, 330),
                AutoSize = true,
                ForeColor = AppTheme.FieldLabel
            };

            var coffeeLink = new LinkLabel
            {
                Text = "buymeacoffee.com/inteliboy",
                Location = new Point(Dpi.S(20) + coffeeLabelW, Dpi.S(330)),
                AutoSize = true,
                LinkColor = AppTheme.Accent,
                ActiveLinkColor = AppTheme.AccentHover,
                VisitedLinkColor = AppTheme.Accent,
                LinkBehavior = LinkBehavior.AlwaysUnderline
            };
            coffeeLink.LinkClicked += delegate
            {
                try { Process.Start("https://buymeacoffee.com/inteliboy"); } catch { }
            };

            var btnClose = new Button
            {
                Text = "Close",
                DialogResult = DialogResult.OK,
                Size = Dpi.Sz(88, 28),
                Location = new Point(ClientSize.Width - Dpi.S(108), ClientSize.Height - Dpi.S(40))
            };
            Ctrl.FlattenButton(btnClose, true);

            var btnCheckUpdates = new Button
            {
                Text = "Check for Updates",
                Size = Dpi.Sz(140, 28),
                Location = new Point(Dpi.S(20), ClientSize.Height - Dpi.S(40))
            };
            Ctrl.FlattenButton(btnCheckUpdates, false);
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
        /// Resizes to exactly (w, h) with AntiAlias + HighQualityBicubic, so a
        /// shrink (or a growth) comes out smooth instead of showing GDI+'s
        /// plain-default staircasing on high-contrast edges. Caller owns the
        /// returned Bitmap's lifetime.
        /// </summary>
        private static Bitmap HighQualityResize(Image src, int w, int h)
        {
            var result = new Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(result))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, w, h));
            }
            return result;
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
                ThemedMessageBox.Show(owner, "Couldn't check for updates right now. Please try again later.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (res.IsUpdateAvailable)
            {
                bool canAutoInstall = !string.IsNullOrEmpty(res.InstallerDownloadUrl);
                var answer = ThemedMessageBox.Show(owner,
                    "A newer version is available.\r\n\r\n" +
                    "You have " + res.CurrentVersion + "; the latest is " + res.LatestVersion + ".\r\n\r\n" +
                    (canAutoInstall
                        ? "Download and install it now? It installs silently in the background and " +
                          "restarts WgSharp when done \u2014 Windows will still ask you to approve the " +
                          "administrator prompt Setup itself requires."
                        : "Open the download page now?"),
                    "WgSharp \u2014 update available",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;

                if (!canAutoInstall)
                {
                    try { Process.Start(res.ReleaseUrl); } catch { }
                    return;
                }

                DownloadAndRunInstaller(owner, res);
                return;
            }

            // No update. Distinguish "confirmed up to date" from "couldn't
            // reach GitHub": LatestVersion is only set when the API answered.
            if (!string.IsNullOrEmpty(res.LatestVersion))
                ThemedMessageBox.Show(owner,
                    "You're up to date.\r\n\r\nWgSharp " + res.CurrentVersion + " is the latest version.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
            else
                ThemedMessageBox.Show(owner,
                    "Couldn't reach GitHub to check for updates. Please check your connection and try again.",
                    "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>
        /// Downloads res.InstallerDownloadUrl (the release's WgSharp-Setup.exe
        /// asset) to a temp file, then launches it SILENTLY ("/S" — see
        /// UpdateChecker.LaunchInstallerSilently) and exits WgSharp so Setup
        /// can replace it cleanly with no wizard for the user to click through.
        /// installer\WgSharp.nsi's own close-detection (CloseWgSharp.ps1) would
        /// handle a still-running WgSharp anyway, but exiting here first is the
        /// smoother path for a user-initiated update: no window flash from
        /// Setup force-closing us mid-flow. Falls back to opening the release
        /// page in a browser on any failure (download error, launch failure) -
        /// never leaves the user stuck with nothing to click.
        /// </summary>
        private static void DownloadAndRunInstaller(IWin32Window owner, WgSharp.Core.UpdateChecker.Result res)
        {
            Control marshalOn = owner as Control;

            WgSharp.Core.UpdateChecker.DownloadInstallerAsync(res.InstallerDownloadUrl, delegate (string localPath, Exception err)
            {
                Action finish = delegate
                {
                    if (err != null || string.IsNullOrEmpty(localPath))
                    {
                        ThemedMessageBox.Show(owner,
                            "Couldn't download the update. Opening the release page instead.",
                            "WgSharp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        try { Process.Start(res.ReleaseUrl); } catch { }
                        return;
                    }
                    try
                    {
                        WgSharp.Core.UpdateChecker.LaunchInstallerSilently(localPath);
                        Application.Exit();
                    }
                    catch
                    {
                        try { Process.Start(res.ReleaseUrl); } catch { }
                    }
                };
                try
                {
                    if (marshalOn != null && marshalOn.IsHandleCreated && marshalOn.InvokeRequired)
                        marshalOn.BeginInvoke(finish);
                    else
                        finish();
                }
                catch { try { finish(); } catch { } }
            });
        }

        /// <summary>
        /// Reads the exe's own embedded version: "1.YY.MMDD.0", stamped by
        /// build.cmd into AssemblyInformationalVersion at build time (see
        /// build.cmd and src/core/AssemblyInfo.generated.cs). The Setup
        /// installer (if built) uses the leading 3 fields of this same date
        /// encoding for its own DisplayVersion, "1.YY.MMDD" -- shown in
        /// Add/Remove Programs -- and both are stamped from the exact same
        /// YY/MM/DD digits in one place in build.cmd.
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

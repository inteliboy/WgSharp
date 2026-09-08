using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WgSharp
{
    internal static class Program
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool IsWow64Process2(IntPtr hProcess, out ushort processMachine, out ushort nativeMachine);

        private const ushort IMAGE_FILE_MACHINE_I386  = 0x014c;
        private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;

        /// <summary>
        /// WgSharp ships an amd64 (x64) build only and runs on x64 hardware
        /// only. Anything else is refused up front: Wintun and WireGuardNT
        /// both install a kernel-mode driver, and Windows' CPU emulation only
        /// covers user-mode code — so an x64 build running under emulation on
        /// ARM64 or any other architecture will never reach a working driver.
        /// Rather than fail confusingly deep inside adapter creation, detect
        /// the mismatch here and exit with a clear message.
        /// IsWow64Process2's pNativeMachine always reports the TRUE underlying
        /// hardware architecture regardless of how the current process is
        /// classified, making it the right probe here.
        /// </summary>
        private static bool IsArchitectureMismatch()
        {
            try
            {
                ushort processMachine, nativeMachine;
                if (!IsWow64Process2(System.Diagnostics.Process.GetCurrentProcess().Handle,
                        out processMachine, out nativeMachine))
                {
                    // API unavailable (pre-Windows 10 1709). Fall back to
                    // process bitness: our x64 build only runs as a 64-bit
                    // process on x64 Windows, so a 32-bit process here means
                    // we're on unsupported hardware.
                    return !Environment.Is64BitProcess || !Environment.Is64BitOperatingSystem;
                }
                return nativeMachine != IMAGE_FILE_MACHINE_AMD64;
            }
            catch { return false; } // never block due to our own detection failing
        }

        [STAThread]
        private static void Main(string[] args)
        {
            // Definitive, not a heuristic: ServiceInstaller registers the
            // service with this exact switch in its binPath, so SCM always
            // launches us with it present. Environment.UserInteractive is the
            // usual way to detect "started by SCM," but it has a real history
            // of being unreliable in some .NET Framework configurations —
            // and if it incorrectly says true for the SYSTEM-context process,
            // that process falls through into the GUI path with no desktop
            // attached (Session 0 has none), which doesn't crash cleanly, it
            // just hangs somewhere in WinForms/icon init and never reaches
            // the actual pipe server. An explicit argument we control
            // ourselves has no such ambiguity.
            if (args.Length > 0 && string.Equals(args[0], "--service", StringComparison.OrdinalIgnoreCase))
            {
                // Load the same settings the GUI persists (same file beside the
                // exe) so the service honors the saved Portable mode /
                // WireGuardNT choice. This used to live in the separate
                // ServiceProgram.cs entry point; it has to be here now that one
                // exe serves both roles, since the GUI's own AppSettings.Load()
                // (in RunApp below) is on the path we DON'T take here.
                WgSharp.Core.AppSettings.Load();
                System.ServiceProcess.ServiceBase.Run(new WgSharp.Svc.WgSharpService());
                return;
            }

            // One-time elevated setup (launched via a "runas" self-start from
            // the unelevated GUI, or manually): registers + starts the
            // background manager service and pre-authorizes the firewall,
            // then exits. Runs BEFORE the single-instance mutex on purpose —
            // the GUI that spawned it is holding that mutex and waiting for
            // this process's exit code.
            if (args.Length > 0 && string.Equals(args[0], "--elevated-setup", StringComparison.OrdinalIgnoreCase))
            {
                WgSharp.Core.AppSettings.Load();
                Environment.ExitCode = WgSharp.Core.ElevatedSetup.Run();
                return;
            }

            // Counterpart to --elevated-setup, used by the Settings checkbox
            // when the (unelevated) GUI is asked to remove the background
            // service: stop and unregister it, then exit.
            if (args.Length > 0 && string.Equals(args[0], "--elevated-uninstall-service", StringComparison.OrdinalIgnoreCase))
            {
                try { WgSharp.Core.ServiceInstaller.Uninstall(); Environment.ExitCode = 0; }
                catch { Environment.ExitCode = 1; }
                return;
            }

            if (IsArchitectureMismatch())
            {
                MessageBox.Show(
                    "WgSharp is built for 64-bit x64 (amd64) Windows only and cannot run " +
                    "on this machine's architecture.\n\n" +
                    "Wintun and WireGuardNT install a kernel-mode driver, and Windows' CPU " +
                    "emulation only covers user-mode code — so an x64 build running under " +
                    "emulation (e.g. on ARM64) will never reach a working driver regardless " +
                    "of how it's launched.",
                    "WgSharp \u2014 unsupported architecture",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Allow only one running instance. A machine-wide (Global\) mutex so
            // it holds across user sessions too, matching the official client's
            // single-instance behavior.
            bool createdNew;
            using (var instanceMutex = new System.Threading.Mutex(true, "Global\\WgSharp_SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    // Another instance holds the mutex. This is normally a
                    // genuine "already running", but it's also the brief
                    // handoff window when a portable copy relaunches itself
                    // elevated: the old (unelevated) instance is exiting and
                    // about to release the mutex as its message loop unwinds.
                    // Wait a short while for it to let go before giving up, so
                    // the elevated relaunch doesn't bounce off its own parent.
                    bool acquired = false;
                    try { acquired = instanceMutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (System.Threading.AbandonedMutexException) { acquired = true; }
                    if (!acquired)
                    {
                        MessageBox.Show("WgSharp is already running.", "WgSharp",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                }
                // "--tray" (set by the login-autostart Run entry) starts the GUI
                // hidden in the notification area instead of showing its window.
                bool startInTray = false;
                for (int i = 0; i < args.Length; i++)
                    if (string.Equals(args[i], "--tray", StringComparison.OrdinalIgnoreCase)) startInTray = true;
                RunApp(startInTray);
            }
        }

        private static void RunApp(bool startInTray)
        {
            // Surface any startup or runtime exception instead of dying silently.
            // A WinForms app launched via an elevation manifest will otherwise just
            // vanish if the form constructor throws.
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            Application.ThreadException += OnThreadException;
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

            try
            {
                // Theme detection/override must happen before EnableVisualStyles()
                // so uxtheme picks up the preferred app mode from the start (same
                // ordering LenovoRepoBuilder's Program.cs uses). AppTheme.IsDark's
                // static initializer already reads the real OS setting; only an
                // explicit user override (persisted separately from other
                // settings, since it's a UI preference) needs to win over that.
                WgSharp.Core.AppSettings.Load();
                if (WgSharp.Core.AppSettings.ThemeOverrideSet)
                    WgSharp.Ui.AppTheme.IsDark = WgSharp.Core.AppSettings.ThemeIsDark;
                WgSharp.Ui.NativeMethods.SetPreferredAppMode(WgSharp.Ui.AppTheme.IsDark);
                WgSharp.Ui.NativeMethods.FlushMenuThemes();

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Must run AFTER Load() (it checks whether a settings file
                // existed to recognize a genuine first run) and BEFORE
                // MainForm is constructed (so the GUI-at-login choice it just
                // made is already in effect for this very launch).
                WgSharp.Core.InstallLocation.ApplyFirstRunDefaultsIfApplicable();
                // Silently re-registers the service if it's missing (see
                // RestoreServiceIfUpgradeWipedIt's own doc comment).
                WgSharp.Core.InstallLocation.RestoreServiceIfUpgradeWipedIt();
                // Unlike the above, this one applies on every launch, not
                // just the first — see its doc comment.
                WgSharp.Core.InstallLocation.EnforcePortableModeRestriction();
                Application.Run(new WgSharp.Ui.MainForm(startInTray));
            }
            catch (Exception ex)
            {
                ShowCrash("Startup", ex);
            }
        }

        private static void OnThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            ShowCrash("UI thread", e.Exception);
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            ShowCrash("Background thread", e.ExceptionObject as Exception);
        }

        private static void ShowCrash(string where, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WgSharp hit an error (" + where + ").");
            sb.AppendLine();
            Exception cur = ex;
            int depth = 0;
            while (cur != null && depth < 6)
            {
                sb.AppendLine(cur.GetType().Name + ": " + cur.Message);
                if (cur.StackTrace != null)
                {
                    string[] lines = cur.StackTrace.Split('\n');
                    for (int i = 0; i < lines.Length && i < 6; i++)
                        sb.AppendLine("    " + lines[i].Trim());
                }
                cur = cur.InnerException;
                if (cur != null) sb.AppendLine("  --- inner ---");
                depth++;
            }

            // Also drop a log file next to the exe for copy/paste.
            try
            {
                string path = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "wgsharp-crash.txt");
                System.IO.File.WriteAllText(path, sb.ToString());
                sb.AppendLine();
                sb.AppendLine("(also written to " + path + ")");
            }
            catch { }

            MessageBox.Show(sb.ToString(), "WgSharp error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

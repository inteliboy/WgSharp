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

        private const ushort IMAGE_FILE_MACHINE_I386 = 0x014c;
        private const ushort IMAGE_FILE_MACHINE_AMD64 = 0x8664;

        /// <summary>
        /// WgSharp now ships an amd64 (x64) build ONLY (see build.cmd), and
        /// runs only on x64 hardware. Anything else is refused up front:
        ///   - Non-x86-family hardware (e.g. ARM64 Windows via its x64/x86
        ///     emulation layer) is genuine instruction-set translation, which
        ///     only covers user-mode code. Wintun/WireGuardNT both install a
        ///     kernel-mode driver, and kernel-mode code is never emulated, so
        ///     there's no path to a working driver there at all.
        ///   - 32-bit x86 hardware is no longer a supported target now that we
        ///     build x64 only; we'd never actually run there (an x64 PE won't
        ///     load on 32-bit Windows), but we still report it clearly if some
        ///     future loader ever tried.
        /// Rather than fail confusingly deep inside adapter creation later,
        /// detect a mismatch up front and refuse to start with a clear
        /// explanation. IsWow64Process2's pNativeMachine always reports the
        /// TRUE underlying hardware architecture, independent of how the
        /// current process itself is classified, so checking it directly is
        /// the right test: we require the native machine to be AMD64.
        /// </summary>
        private static bool IsArchitectureMismatch()
        {
            try
            {
                ushort processMachine, nativeMachine;
                if (!IsWow64Process2(System.Diagnostics.Process.GetCurrentProcess().Handle,
                        out processMachine, out nativeMachine))
                {
                    // API unavailable (pre-Windows 10 1709). Fall back to the
                    // process bitness: an x64 build only ever runs as a 64-bit
                    // process on x64 Windows, so a 32-bit process here means we
                    // somehow landed somewhere unsupported. Don't hard-block on
                    // uncertainty though — only block the clearly-wrong case.
                    return !Environment.Is64BitProcess && !Environment.Is64BitOperatingSystem;
                }
                // amd64 only.
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

            if (IsArchitectureMismatch())
            {
                MessageBox.Show(
                    "WgSharp is built for 64-bit x64 (amd64) only and can't run on " +
                    "this machine's architecture.\n\n" +
                    "This isn't a missing download or a setting to change: Wintun/WireGuardNT " +
                    "install a kernel-mode driver, and Windows' CPU emulation only covers " +
                    "user-mode code, so an x64 build can never reach a working driver on " +
                    "non-x64 hardware regardless of how it's run.",
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
                    MessageBox.Show("WgSharp is already running.", "WgSharp",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                WgSharp.Core.AppSettings.Load();
                // Must run AFTER Load() (it checks whether a settings file
                // existed to recognize a genuine first run) and BEFORE
                // MainForm is constructed (so the GUI-at-login choice it just
                // made is already in effect for this very launch).
                WgSharp.Core.InstallLocation.ApplyFirstRunDefaultsIfApplicable();
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

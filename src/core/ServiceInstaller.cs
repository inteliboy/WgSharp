using System;
using System.Diagnostics;
using System.Reflection;
using System.IO;

namespace WgSharp.Core
{
    /// <summary>
    /// Installs/uninstalls the background service via sc.exe — the same
    /// "shell out to a Windows command-line tool, we're already elevated"
    /// pattern already used for firewall self-registration. No MSBuild
    /// installer project, no System.Configuration.Install dependency.
    /// </summary>
    public static class ServiceInstaller
    {
        private const string ServiceName = "WgSharpSvc";

        private static string ServiceExePath
        {
            // The same exe, not a separate one: Program.cs's Main() checks
            // for a "--service" argument (see BinPathValue below, which is what
            // gets registered) and runs as the service instead of the GUI
            // when it's present.
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        // The exact command line SCM will launch. The "--service" argument
        // is what Main() checks — deliberately not relying on
        // Environment.UserInteractive alone, which has a real history of
        // being unreliable in some .NET Framework configurations. If it
        // incorrectly reports true for the SYSTEM-context process SCM
        // starts, that process falls through into the GUI path with no
        // desktop attached, which doesn't fail cleanly — it just hangs
        // somewhere in WinForms/icon initialization and never reaches the
        // actual pipe server. An argument we control ourselves at
        // registration time has no such ambiguity.
        //
        // sc.exe's binPath value, since it contains a space (between the
        // quoted exe path and "--service"), must itself be wrapped in an
        // outer pair of quotes with the exe path's own quotes escaped —
        // otherwise sc/cmd parsing splits it apart at that space. This is
        // the standard, slightly fiddly pattern for registering a service
        // with arguments: binPath= "\"C:\...\WgSharp.exe\" --service".
        private static string BinPathValue
        {
            get { return "\"\\\"" + ServiceExePath + "\\\" --service\""; }
        }

        public static bool ServiceExeExists { get { return File.Exists(ServiceExePath); } }

        /// <summary>True if a service named WgSharpSvc is registered with SCM (installed; not necessarily running).</summary>
        public static bool IsInstalled()
        {
            try
            {
                string output = RunSc("query " + ServiceName, true);
                // sc query on a nonexistent service prints "...specified service
                // does not exist..." and a nonzero exit code; RunSc returns null
                // on a nonzero exit when ignoreErrors is requested, distinguishing
                // "doesn't exist" from "exists but stopped" (which still exits 0
                // with STATE: STOPPED in the output).
                return output != null;
            }
            catch { return false; }
        }

        public static void Install()
        {
            if (!ServiceExeExists)
                throw new Exception("Could not resolve WgSharp.exe's own path on disk.");

            // Register (or re-point) the service with SCM. Deliberately does
            // NOT start it: starting the service is what activates a tunnel,
            // and there may be no tunnel selected at install time. Runtime
            // start/stop is handled separately (StartTunnelService /
            // StopTunnelService), modeled on the official WireGuard client,
            // where the Manager installs the tunnel service and then asks SCM
            // to start it as a distinct step.
            //
            // start= demand, NOT auto: the GUI starts the service explicitly
            // when you click Activate. (Boot-time auto-reconnect is handled by
            // flipping this to auto only while a tunnel is connected — see
            // SetBootStart — so a reboot reconnects the last tunnel, but the
            // service doesn't pointlessly start at boot when nothing was on.)
            if (IsInstalled())
                RunSc("config " + ServiceName + " binPath= " + BinPathValue, false);
            else
                RunSc("create " + ServiceName +
                      " binPath= " + BinPathValue +
                      " start= demand" +
                      " obj= LocalSystem" +
                      " DisplayName= \"WgSharp Tunnel Service\"", false);
        }

        public static void Uninstall()
        {
            StopTunnelService();                    // graceful stop if running
            RunSc("delete " + ServiceName, true);   // ignore errors: e.g. "doesn't exist"
        }

        /// <summary>
        /// Starts the service, which activates the tunnel named in ServiceState
        /// — the actual adapter/route/driver bring-up happens inside the
        /// service's OnStart, with SCM managing the multi-second start. Blocks
        /// until the service reports Running or the timeout elapses. Throws on
        /// failure so the GUI can surface it.
        /// </summary>
        public static void StartTunnelService()
        {
            using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
            {
                sc.Refresh();
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                    return; // already up
                if (sc.Status != System.ServiceProcess.ServiceControllerStatus.StartPending)
                    sc.Start();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running,
                    TimeSpan.FromSeconds(30));
            }
        }

        /// <summary>Stops the service (deactivates the tunnel). Blocks until Stopped or timeout. Safe if already stopped.</summary>
        public static void StopTunnelService()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
                {
                    sc.Refresh();
                    if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Stopped)
                        return;
                    if (sc.CanStop)
                    {
                        sc.Stop();
                        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,
                            TimeSpan.FromSeconds(30));
                    }
                }
            }
            catch { /* not installed / already gone — nothing to stop */ }
        }

        /// <summary>True if the service is installed AND currently running.</summary>
        public static bool IsRunning()
        {
            try
            {
                using (var sc = new System.ServiceProcess.ServiceController(ServiceName))
                {
                    sc.Refresh();
                    return sc.Status == System.ServiceProcess.ServiceControllerStatus.Running
                        || sc.Status == System.ServiceProcess.ServiceControllerStatus.StartPending;
                }
            }
            catch { return false; }
        }

        /// <summary>
        /// Switches the service's start type between auto (reconnect at boot)
        /// and demand (only when the GUI starts it). The GUI sets auto while a
        /// tunnel is connected through the service and demand when it's not, so
        /// a reboot restores exactly the last connection state — connected
        /// stays connected, disconnected stays disconnected — matching the
        /// "restore last status" behavior without starting pointlessly at boot.
        /// </summary>
        public static void SetBootStart(bool autoStart)
        {
            if (!IsInstalled()) return;
            RunSc("config " + ServiceName + " start= " + (autoStart ? "auto" : "demand"), true);
        }

        /// <summary>
        /// If the service is installed but registered with a different
        /// binPath than what we'd register right now (e.g. an earlier
        /// extraction folder, or a version from before this argument existed),
        /// re-point it so a stale installation self-heals without the user
        /// needing to manually toggle the Settings checkbox off and back on.
        /// Called once at GUI startup. Does not start the service (Install no
        /// longer does either) — it only fixes the registration. Returns true
        /// if a refresh actually happened.
        /// </summary>
        public static bool RefreshIfStale()
        {
            if (!IsInstalled()) return false;
            string configured = GetConfiguredBinPath();
            string current = ServiceExePath + " --service";
            // sc qc's BINARY_PATH_NAME echoes back without the outer quoting
            // we send (just the literal path + args), so compare against the
            // unquoted form rather than BinPathValue.
            if (configured != null &&
                NormalizeBinPath(configured) == NormalizeBinPath(current))
                return false; // already up to date
            Install(); // re-point the registration (no start)
            return true;
        }

        private static string NormalizeBinPath(string s)
        {
            return s.Replace("\"", "").Trim().ToLowerInvariant();
        }

        private static string GetConfiguredBinPath()
        {
            try
            {
                string output = RunSc("qc " + ServiceName, true);
                if (output == null) return null;
                foreach (string line in output.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                    {
                        int idx = t.IndexOf(':');
                        return idx >= 0 ? t.Substring(idx + 1).Trim() : null;
                    }
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Runs sc.exe with the given arguments. Returns stdout on success
        /// (exit code 0), or null on failure. If ignoreErrors is true, a
        /// failure is swallowed (returns null) instead of throwing.
        /// </summary>
        private static string RunSc(string args, bool ignoreErrors)
        {
            var psi = new ProcessStartInfo("sc.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string errp = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0)
                {
                    if (ignoreErrors) return null;
                    throw new Exception("sc.exe " + args + " -> exit " + p.ExitCode + ": " +
                                        (errp.Length > 0 ? errp : outp));
                }
                return outp;
            }
        }
    }
}

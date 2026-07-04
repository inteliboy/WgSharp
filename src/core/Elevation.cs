using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;

namespace WgSharp.Core
{
    /// <summary>
    /// Elevation helpers for the asInvoker GUI. The manifest no longer
    /// requests administrator, so the process usually is NOT elevated; the
    /// few remaining privileged code paths (in-process tunnel fallback,
    /// firewall self-registration, service registration) check
    /// IsProcessElevated before attempting anything, and first-time service
    /// setup goes through RunElevatedSetup — a single "runas" self-launch
    /// that shows one UAC prompt, once, instead of one on every start.
    /// </summary>
    public static class Elevation
    {
        /// <summary>
        /// True when THIS process is elevated (its token's Administrators
        /// membership is enabled). An admin user running unelevated returns
        /// FALSE here — UAC marks the Administrators SID deny-only in the
        /// filtered token — which is exactly the question the callers are
        /// asking ("can I do privileged work right now"), as opposed to "is
        /// this user an admin at all" (that question belongs to the service,
        /// which answers it against the client's linked token — see
        /// PipeClientAuth).
        /// </summary>
        public static bool IsProcessElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>
        /// Relaunches this exe elevated with --elevated-setup and waits for it
        /// to finish: registers + starts the background service and
        /// pre-authorizes the firewall (see ElevatedSetup.Run). Returns true
        /// when the setup process ran and reported success. "error" explains
        /// a false return: "cancelled" when the user dismissed the UAC
        /// prompt, otherwise a short failure description.
        /// </summary>
        public static bool RunElevatedSetup(out string error)
        {
            return RunElevated("--elevated-setup", out error);
        }

        /// <summary>
        /// Relaunches this exe elevated with its normal (GUI) arguments and
        /// does NOT wait — the caller exits its own process so the elevated
        /// copy takes over (used by portable mode's "relaunch as administrator"
        /// offer). Returns true if the elevated process was started; on false,
        /// "error" is "cancelled" when the user dismissed the UAC prompt.
        /// </summary>
        public static bool RelaunchElevated(out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    UseShellExecute = true,
                    Verb = "runas"
                };
                Process p = Process.Start(psi);
                return p != null;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                error = "cancelled"; // UAC declined (ERROR_CANCELLED)
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Relaunches this exe elevated with the given arguments and waits.
        /// Used for the rare admin-only actions that remain after the
        /// asInvoker switch (setup, removing the service).
        /// </summary>
        public static bool RunElevated(string arguments, out string error)
        {
            error = null;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Assembly.GetExecutingAssembly().Location,
                    Arguments = arguments,
                    UseShellExecute = true, // required for the runas verb
                    Verb = "runas"
                };
                using (Process p = Process.Start(psi))
                {
                    if (p == null) { error = "could not start the setup process"; return false; }
                    // Registering a service + two netsh calls takes a couple
                    // of seconds; two minutes is a generous ceiling that still
                    // prevents the GUI hanging forever on a wedged child.
                    if (!p.WaitForExit(120000)) { error = "setup timed out"; return false; }
                    if (p.ExitCode != 0) { error = "setup exited with code " + p.ExitCode; return false; }
                    return true;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // The user clicked "No" on the UAC prompt (ERROR_CANCELLED).
                error = "cancelled";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}

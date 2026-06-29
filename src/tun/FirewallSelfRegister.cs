using System;
using System.Diagnostics;

namespace WgSharp.Tun
{
    /// <summary>
    /// Pre-authorizes the running executable with Windows Firewall (inbound +
    /// outbound, all profiles) at startup, so Windows never needs to show the
    /// interactive "allow this app on private/public networks" consent dialog.
    ///
    /// Why this exists: that prompt is keyed to the program's path/identity and
    /// fires the first time a process does something the firewall hasn't made a
    /// decision about yet (most commonly accepting inbound traffic). We saw it
    /// appear only when using the WireGuardNT backend and not the managed one,
    /// even for the identical exe — the most likely explanation is that
    /// WireGuardNT's kernel driver opens its own listening UDP endpoint on the
    /// process's behalf, which Windows evaluates separately from a plain
    /// Winsock socket our managed backend uses directly. Rather than depend on
    /// pinning down that mechanism exactly, this sidesteps it: once an explicit
    /// allow rule exists for the program, Windows has nothing left to ask about.
    ///
    /// The app already runs elevated (requireAdministrator in app.manifest), so
    /// this can run silently at every startup with no extra UAC step. The rule
    /// is rewritten each run to point at the CURRENT exe path, so it keeps
    /// working even if the exe is moved or extracted to a new folder.
    /// </summary>
    public static class FirewallSelfRegister
    {
        public static event Action<string> Log;
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Firewall")); }

        private const string InRuleName = "WgSharp-in";
        private const string OutRuleName = "WgSharp-out";

        public static void EnsureRulesForCurrentExe()
        {
            string exePath;
            try { exePath = Process.GetCurrentProcess().MainModule.FileName; }
            catch (Exception ex) { L("Could not resolve own exe path: " + ex.Message); return; }

            try
            {
                // Remove any stale rule first (e.g. left over from a previous
                // location of this app), so the rule always matches what's running
                // now rather than accumulating duplicates.
                RunNetsh("advfirewall firewall delete rule name=\"" + InRuleName + "\"", true);
                RunNetsh("advfirewall firewall delete rule name=\"" + OutRuleName + "\"", true);

                RunNetsh("advfirewall firewall add rule name=\"" + InRuleName + "\" dir=in action=allow " +
                         "program=\"" + exePath + "\" profile=any enable=yes", false);
                RunNetsh("advfirewall firewall add rule name=\"" + OutRuleName + "\" dir=out action=allow " +
                         "program=\"" + exePath + "\" profile=any enable=yes", false);

                L(WgSharp.Core.Logger.DebugMarker + "Pre-authorized with Windows Firewall (in+out, all profiles) for " + exePath +
                  " \u2014 the interactive 'allow this app' prompt should no longer appear.");
            }
            catch (Exception ex)
            {
                L("Could not pre-register firewall rules (" + ex.Message +
                  "); the interactive prompt may still appear.");
            }
        }

        private static void RunNetsh(string args, bool ignoreErrors)
        {
            var psi = new ProcessStartInfo("netsh", args)
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
                if (p.ExitCode != 0 && !ignoreErrors)
                    throw new Exception("netsh " + args + " -> exit " + p.ExitCode + ": " +
                                        (errp.Length > 0 ? errp : outp));
            }
        }
    }
}

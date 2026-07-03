using System;

namespace WgSharp.Core
{
    /// <summary>
    /// The ONE elevated thing left in WgSharp's lifetime: a short, run-once
    /// helper (invoked as "WgSharp.exe --elevated-setup" through a UAC
    /// prompt) that puts the machine into the state where the unelevated GUI
    /// works forever after:
    ///
    ///   1. Registers the background manager service (start= auto) and starts
    ///      it — from then on, activation/deactivation are pipe commands and
    ///      never need elevation again.
    ///   2. Pre-authorizes this exe with Windows Firewall, so the interactive
    ///      "allow this app" consent dialog (itself an elevated operation)
    ///      never fires later.
    ///
    /// Driver bootstrap deliberately does NOT run here: the service performs
    /// it in its own OnStart (as LocalSystem it can write next to the exe in
    /// Program Files), so setup stays quick and the downloads keep their
    /// existing retry/verification path.
    ///
    /// The process is a winexe with no console; results go to the service
    /// error log directory and the exit code (0 = success), which the GUI
    /// reports in its Log tab.
    /// </summary>
    public static class ElevatedSetup
    {
        public static int Run()
        {
            int failures = 0;

            try
            {
                ServiceInstaller.EnsureInstalledAndRunning();
                Note("Background service registered (start=auto) and running.");
            }
            catch (Exception ex)
            {
                failures++;
                Note("Service setup FAILED: " + ex.Message);
            }

            try
            {
                WgSharp.Tun.FirewallSelfRegister.EnsureRulesForCurrentExe();
                Note("Windows Firewall rules ensured for this exe.");
            }
            catch (Exception ex)
            {
                failures++;
                Note("Firewall pre-authorization FAILED: " + ex.Message);
            }

            return failures == 0 ? 0 : 1;
        }

        private static void Note(string message)
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WgSharp");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(dir, "setup.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [Setup] " + message + "\r\n");
            }
            catch { }
        }
    }
}

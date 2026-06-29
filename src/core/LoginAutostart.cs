using System;
using System.Reflection;
using Microsoft.Win32;

namespace WgSharp.Core
{
    /// <summary>
    /// Manages whether the WgSharp GUI launches automatically when the user
    /// logs in, mirroring the official WireGuard client (whose UI also appears
    /// in the tray on login, separately from the boot-time tunnel service).
    ///
    /// This is the GUI, not the service: the background service handles
    /// reconnecting the last tunnel before login (see ServiceInstaller /
    /// SetBootStart), while THIS controls the tray app showing up for the
    /// logged-in user. They're independent — you can have either, both, or
    /// neither.
    ///
    /// Implemented as a per-user Run entry under
    /// HKCU\Software\Microsoft\Windows\CurrentVersion\Run, which Windows
    /// executes at interactive login. The registered command adds the
    /// "--tray" argument so the app starts hidden to the notification area
    /// instead of popping its window open on every login. No elevation needed
    /// (HKCU is the current user's own hive), and nothing machine-wide is
    /// touched.
    /// </summary>
    public static class LoginAutostart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WgSharp";

        private static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        // The exact command written to the Run value: the quoted exe path
        // followed by --tray so the GUI starts minimized to the tray.
        private static string RunCommand
        {
            get { return "\"" + ExePath + "\" --tray"; }
        }

        /// <summary>True if the GUI is registered to start at this user's login.</summary>
        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null;
                }
            }
            catch { return false; }
        }

        public static void Enable()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) throw new Exception("Could not open the per-user Run registry key.");
                key.SetValue(ValueName, RunCommand, RegistryValueKind.String);
            }
        }

        public static void Disable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, false);
                }
            }
            catch { /* best-effort; nothing registered is the same as disabled */ }
        }

        /// <summary>
        /// If autostart is enabled but points at a different exe path than this
        /// one (e.g. the app was moved to a new folder), re-point it so the
        /// entry keeps working. Called once at GUI startup, same idea as
        /// ServiceInstaller.RefreshIfStale for the service.
        /// </summary>
        public static void RefreshIfStale()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null) return;
                    object v = key.GetValue(ValueName);
                    if (v == null) return; // not enabled; nothing to refresh
                    if (!string.Equals(v.ToString(), RunCommand, StringComparison.OrdinalIgnoreCase))
                        key.SetValue(ValueName, RunCommand, RegistryValueKind.String);
                }
            }
            catch { }
        }
    }
}

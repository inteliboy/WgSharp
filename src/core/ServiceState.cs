using System;
using Microsoft.Win32;

namespace WgSharp.Core
{
    /// <summary>
    /// Remembers which tunnel was last successfully activated, for the
    /// background service to reconnect to on its next start (including at
    /// boot, before anyone logs in). Cleared on an explicit disconnect.
    ///
    /// Stored in HKEY_LOCAL_MACHINE\Software\WgSharp (LastTunnel) so:
    ///   - It survives MSI upgrades without special installer logic.
    ///   - The background service (LocalSystem) can read the same value the
    ///     GUI (an elevated user process) writes — no session boundary issues.
    ///   - No ProgramData directory creation needed.
    ///
    /// Only ever holds a tunnel name (never key material), so reading it from
    /// HKLM is not a security concern — the actual config is still protected
    /// by DPAPI in ConfigStore.
    /// </summary>
    public static class ServiceState
    {
        private const string RegKey   = @"Software\WgSharp";
        private const string ValueName = "LastTunnel";

        public static void SetLastTunnel(string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(RegKey))
                    if (k != null) k.SetValue(ValueName, name ?? "", RegistryValueKind.String);
            }
            catch { }
        }

        public static void Clear()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(RegKey, true))
                    if (k != null) k.DeleteValue(ValueName, false);
            }
            catch { }
        }

        /// <summary>Returns the last tunnel name, or null if there isn't one.</summary>
        public static string GetLastTunnel()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(RegKey, false))
                {
                    if (k == null) return null;
                    object v = k.GetValue(ValueName);
                    if (v == null) return null;
                    string s = v.ToString().Trim();
                    return s.Length == 0 ? null : s;
                }
            }
            catch { return null; }
        }
    }
}

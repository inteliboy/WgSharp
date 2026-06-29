using System;
using System.IO;

namespace WgSharp.Core
{
    /// <summary>
    /// Remembers which tunnel was last successfully activated, for the
    /// background service to reconnect to on its next start (including at
    /// boot, before anyone logs in). Cleared on an explicit disconnect, so
    /// "restore last connection status" means exactly that: if you were
    /// connected when the machine went down, it reconnects; if you'd already
    /// disconnected, it doesn't.
    ///
    /// Just a plain text file with a tunnel name — not sensitive on its own
    /// (the actual config stays wherever ConfigStore already protects it;
    /// this only ever names a non-portable, machine-DPAPI tunnel, since
    /// that's the only kind the service can decrypt without a human present
    /// to type a password).
    /// </summary>
    public static class ServiceState
    {
        private static string FilePath
        {
            get
            {
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(Path.Combine(programData, "WgSharp"), "last_tunnel.txt");
            }
        }

        public static void SetLastTunnel(string name)
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(FilePath, name ?? "");
            }
            catch { }
        }

        public static void Clear()
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); }
            catch { }
        }

        /// <summary>Returns the last tunnel name, or null if there isn't one.</summary>
        public static string GetLastTunnel()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string s = File.ReadAllText(FilePath).Trim();
                return s.Length == 0 ? null : s;
            }
            catch { return null; }
        }
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace WgSharp.Core
{
    /// <summary>
    /// Lightweight persisted settings, as a tiny key=value text file.
    /// Location depends on how WgSharp is running (see InstallLocation):
    /// next to the executable normally (so portable mode is genuinely
    /// self-contained — settings travel with the app folder), or under
    /// ProgramData when running from the MSI's fixed install location,
    /// matching where ConfigStore already keeps non-portable tunnel configs.
    /// </summary>
    public static class AppSettings
    {
        public static bool PortableMode;
        public static bool UseWireGuardNt = true;    // use kernel WireGuardNT backend (default)
        public static bool DebugLog;                 // verbose Log tab output + service log file
        public static bool StartGuiAtLogin;          // launch the GUI (to tray) on user login

        private static string ExeDir
        {
            get
            {
                string path = Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(path);
            }
        }

        private static string SettingsPath
        {
            get
            {
                // Installed (Program Files, fixed location): use ProgramData,
                // like ConfigStore already does for non-portable tunnel
                // configs — Program Files isn't really meant to be written to
                // on an ongoing basis, even though our manifest forces
                // elevation. Any other context (the zip distribution, a dev
                // build, portable mode's own folder) keeps using the exe's own
                // directory, so settings still travel with the app folder.
                string dir;
                if (InstallLocation.IsInstalled())
                {
                    string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                    dir = Path.Combine(programData, "WgSharp");
                    try { Directory.CreateDirectory(dir); } catch { }
                }
                else
                {
                    dir = ExeDir;
                }
                return Path.Combine(dir, "WgSharp.settings");
            }
        }

        /// <summary>
        /// True if a settings file already exists on disk. Checked BEFORE
        /// calling Load() to distinguish a genuine first run (no settings
        /// file yet) from a normal launch — used by MainForm to decide
        /// whether to apply InstallLocation's first-run defaults.
        /// </summary>
        public static bool SettingsFileExists
        {
            get { try { return File.Exists(SettingsPath); } catch { return true; } } // assume "existing" on error, the safer default (skip first-run actions)
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                foreach (string raw in File.ReadAllLines(SettingsPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("PortableMode", StringComparison.OrdinalIgnoreCase))
                        PortableMode = ParseBool(val);
                    else if (key.Equals("UseWireGuardNt", StringComparison.OrdinalIgnoreCase))
                        UseWireGuardNt = ParseBool(val);
                    else if (key.Equals("DebugLog", StringComparison.OrdinalIgnoreCase))
                        DebugLog = ParseBool(val);
                    else if (key.Equals("StartGuiAtLogin", StringComparison.OrdinalIgnoreCase))
                        StartGuiAtLogin = ParseBool(val);
                }
            }
            catch { /* defaults on any error */ }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# WgSharp settings");
                sb.AppendLine("PortableMode=" + (PortableMode ? "true" : "false"));
                sb.AppendLine("UseWireGuardNt=" + (UseWireGuardNt ? "true" : "false"));
                sb.AppendLine("DebugLog=" + (DebugLog ? "true" : "false"));
                sb.AppendLine("StartGuiAtLogin=" + (StartGuiAtLogin ? "true" : "false"));
                File.WriteAllText(SettingsPath, sb.ToString());
            }
            catch { /* best-effort */ }
        }

        private static bool ParseBool(string v)
        {
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}

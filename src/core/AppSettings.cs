using System;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace WgSharp.Core
{
    /// <summary>
    /// Persisted settings. Storage location depends on run mode:
    ///
    ///   Non-portable (installed or zip without PortableMode):
    ///     HKEY_LOCAL_MACHINE\Software\WgSharp
    ///     Written/read with the process's existing elevation (LocalMachine
    ///     requires admin, which WgSharp's manifest already demands).
    ///     Machine-scoped so the background service (LocalSystem) can read
    ///     the same values the GUI writes — no per-user divergence.
    ///
    ///   Portable mode:
    ///     A plain key=value text file next to the executable, so the whole
    ///     app folder is self-contained and travels without touching the
    ///     registry on the host machine.
    ///
    /// PortableMode itself is always checked in the file first (it's what
    /// tells us which store to use for everything else). If the file says
    /// PortableMode=true, the file IS the store. Otherwise we use the
    /// registry, and the file (if it exists) is ignored for all other keys.
    /// </summary>
    public static class AppSettings
    {
        public static bool PortableMode;
        public static bool UseWireGuardNt = true;
        public static bool DebugLog;
        public static bool CheckForUpdates = true;   // check GitHub for a newer release at startup
        public static bool StartGuiAtLogin;
        public static bool ServiceWasInstalled;
        public static string TunnelOrder = "";
        // Dark/light theme override. ThemeOverrideSet is false until the user
        // explicitly toggles the "Dark theme" setting; while false, the app
        // follows the real OS theme instead of ThemeIsDark.
        public static bool ThemeIsDark;
        public static bool ThemeOverrideSet;
        // Auto-connect rules: bring AutoConnectTunnel up on any network that
        // isn't trusted (a Wi-Fi SSID in TrustedNetworks, or wired Ethernet when
        // WiredIsTrusted), and take it down again on a trusted one.
        public static bool AutoConnectEnabled;
        public static string AutoConnectTunnel = "";
        public static string TrustedNetworks = "";   // comma/semicolon separated SSIDs
        public static bool WiredIsTrusted;

        private const string RegKey = @"Software\WgSharp";

        // ------------------------------------------------------------------ //
        //  Store selection                                                     //
        // ------------------------------------------------------------------ //

        private static bool UseRegistry { get { return !PortableMode; } }

        private static string FileSettingsPath
        {
            get
            {
                string exe = Assembly.GetExecutingAssembly().Location;
                return Path.Combine(Path.GetDirectoryName(exe), "WgSharp.settings");
            }
        }

        /// <summary>
        /// True if settings have been saved before (used to detect first run).
        /// For registry mode: key exists. For file mode: file exists.
        /// </summary>
        public static bool SettingsFileExists
        {
            get
            {
                try
                {
                    // Always check file first so we know whether PortableMode is set.
                    if (File.Exists(FileSettingsPath)) return true;
                    // If no file, check registry.
                    using (var k = Registry.LocalMachine.OpenSubKey(RegKey, false))
                        return k != null;
                }
                catch { return true; } // assume existing on error (skip first-run defaults)
            }
        }

        // ------------------------------------------------------------------ //
        //  Load                                                                //
        // ------------------------------------------------------------------ //

        public static void Load()
        {
            // Step 1: always read the file to learn whether PortableMode is on.
            LoadPortableFlagFromFile();

            if (UseRegistry)
                LoadFromRegistry();
            else
                LoadFromFile(); // portable: file is the only store
        }

        private static void LoadPortableFlagFromFile()
        {
            try
            {
                if (!File.Exists(FileSettingsPath)) return;
                foreach (string raw in File.ReadAllLines(FileSettingsPath))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line.Substring(0, eq).Trim().Equals("PortableMode", StringComparison.OrdinalIgnoreCase))
                    {
                        PortableMode = ParseBool(line.Substring(eq + 1).Trim());
                        return;
                    }
                }
            }
            catch { }
        }

        private static void LoadFromRegistry()
        {
            // One-time migration: if the old text-file settings exist but the
            // registry key doesn't yet, copy everything across and delete the
            // file. This runs exactly once for existing users upgrading from a
            // file-based build; afterward the file is gone and only the
            // registry is used. Safe to run on every startup — if the registry
            // key already exists, MigrateFileToRegistry is a no-op.
            MigrateFileToRegistry();

            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(RegKey, false))
                {
                    if (k == null) return;
                    UseWireGuardNt      = ReadBool(k, "UseWireGuardNt", true);
                    DebugLog            = ReadBool(k, "DebugLog", false);
                    CheckForUpdates     = ReadBool(k, "CheckForUpdates", true);
                    StartGuiAtLogin     = ReadBool(k, "StartGuiAtLogin", false);
                    ServiceWasInstalled = ReadBool(k, "ServiceWasInstalled", false);
                    TunnelOrder         = ReadString(k, "TunnelOrder", "");
                    ThemeIsDark         = ReadBool(k, "ThemeIsDark", false);
                    ThemeOverrideSet    = ReadBool(k, "ThemeOverrideSet", false);
                    AutoConnectEnabled  = ReadBool(k, "AutoConnectEnabled", false);
                    AutoConnectTunnel   = ReadString(k, "AutoConnectTunnel", "");
                    TrustedNetworks     = ReadString(k, "TrustedNetworks", "");
                    WiredIsTrusted      = ReadBool(k, "WiredIsTrusted", false);
                }
            }
            catch { }
        }

        private static void MigrateFileToRegistry()
        {
            try
            {
                // Already migrated (registry key exists) — nothing to do.
                using (var existing = Registry.LocalMachine.OpenSubKey(RegKey, false))
                    if (existing != null) return;

                // Old file locations: ProgramData\WgSharp\WgSharp.settings (installed)
                // or beside the exe (zip). Check both.
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                string[] candidates = {
                    Path.Combine(programData, "WgSharp", "WgSharp.settings"),
                    FileSettingsPath,
                };

                string found = null;
                foreach (string c in candidates)
                    if (File.Exists(c)) { found = c; break; }
                if (found == null) return;

                // Parse the file temporarily into local vars, then write registry.
                bool wgNt = true, debug = false, gui = false, svc = false, upd = true;
                string order = "";
                foreach (string raw in File.ReadAllLines(found))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (key.Equals("UseWireGuardNt", StringComparison.OrdinalIgnoreCase))       wgNt  = ParseBool(val);
                    else if (key.Equals("DebugLog", StringComparison.OrdinalIgnoreCase))         debug = ParseBool(val);
                    else if (key.Equals("CheckForUpdates", StringComparison.OrdinalIgnoreCase)) upd = ParseBool(val);
                    else if (key.Equals("StartGuiAtLogin", StringComparison.OrdinalIgnoreCase))  gui   = ParseBool(val);
                    else if (key.Equals("ServiceWasInstalled", StringComparison.OrdinalIgnoreCase)) svc = ParseBool(val);
                    else if (key.Equals("TunnelOrder", StringComparison.OrdinalIgnoreCase))      order = val;
                    // PortableMode=true in this file means we're in portable mode
                    // and shouldn't be here — but we already checked UseRegistry
                    // before calling LoadFromRegistry(), so it's safe to ignore it.
                }

                using (var k = Registry.LocalMachine.CreateSubKey(RegKey))
                {
                    if (k == null) return;
                    k.SetValue("UseWireGuardNt",       wgNt  ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("DebugLog",             debug ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("CheckForUpdates",      upd   ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("StartGuiAtLogin",      gui   ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("ServiceWasInstalled",  svc   ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("TunnelOrder",          order,          RegistryValueKind.String);
                }

                // Also migrate last_tunnel.txt → registry if present.
                string lastTunnelFile = Path.Combine(programData, "WgSharp", "last_tunnel.txt");
                if (File.Exists(lastTunnelFile))
                {
                    string lt = File.ReadAllText(lastTunnelFile).Trim();
                    if (lt.Length > 0) ServiceState.SetLastTunnel(lt);
                    try { File.Delete(lastTunnelFile); } catch { }
                }

                // Delete the settings file now that everything is in the registry.
                // Best-effort: if it fails (permissions, locked), it's harmless —
                // the registry already has the values and takes precedence.
                try { File.Delete(found); } catch { }
            }
            catch { /* migration is best-effort; never block startup */ }
        }

        private static void LoadFromFile()
        {
            try
            {
                if (!File.Exists(FileSettingsPath)) return;
                foreach (string raw in File.ReadAllLines(FileSettingsPath))
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
                    else if (key.Equals("CheckForUpdates", StringComparison.OrdinalIgnoreCase))
                        CheckForUpdates = ParseBool(val);
                    else if (key.Equals("StartGuiAtLogin", StringComparison.OrdinalIgnoreCase))
                        StartGuiAtLogin = ParseBool(val);
                    else if (key.Equals("ServiceWasInstalled", StringComparison.OrdinalIgnoreCase))
                        ServiceWasInstalled = ParseBool(val);
                    else if (key.Equals("TunnelOrder", StringComparison.OrdinalIgnoreCase))
                        TunnelOrder = val;
                    else if (key.Equals("ThemeIsDark", StringComparison.OrdinalIgnoreCase))
                        ThemeIsDark = ParseBool(val);
                    else if (key.Equals("ThemeOverrideSet", StringComparison.OrdinalIgnoreCase))
                        ThemeOverrideSet = ParseBool(val);
                    else if (key.Equals("AutoConnectEnabled", StringComparison.OrdinalIgnoreCase))
                        AutoConnectEnabled = ParseBool(val);
                    else if (key.Equals("AutoConnectTunnel", StringComparison.OrdinalIgnoreCase))
                        AutoConnectTunnel = val;
                    else if (key.Equals("TrustedNetworks", StringComparison.OrdinalIgnoreCase))
                        TrustedNetworks = val;
                    else if (key.Equals("WiredIsTrusted", StringComparison.OrdinalIgnoreCase))
                        WiredIsTrusted = ParseBool(val);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ //
        //  Save                                                                //
        // ------------------------------------------------------------------ //

        public static void Save()
        {
            if (UseRegistry)
                SaveToRegistry();
            else
                SaveToFile();
        }

        private static void SaveToRegistry()
        {
            try
            {
                using (var k = Registry.LocalMachine.CreateSubKey(RegKey))
                {
                    if (k == null) return;
                    k.SetValue("UseWireGuardNt",       UseWireGuardNt       ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("DebugLog",             DebugLog             ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("CheckForUpdates",      CheckForUpdates      ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("StartGuiAtLogin",      StartGuiAtLogin      ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("ServiceWasInstalled",  ServiceWasInstalled  ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("TunnelOrder",          TunnelOrder ?? "",            RegistryValueKind.String);
                    k.SetValue("ThemeIsDark",          ThemeIsDark          ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("ThemeOverrideSet",     ThemeOverrideSet     ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AutoConnectEnabled",   AutoConnectEnabled   ? 1 : 0, RegistryValueKind.DWord);
                    k.SetValue("AutoConnectTunnel",    AutoConnectTunnel ?? "",      RegistryValueKind.String);
                    k.SetValue("TrustedNetworks",      TrustedNetworks ?? "",        RegistryValueKind.String);
                    k.SetValue("WiredIsTrusted",       WiredIsTrusted       ? 1 : 0, RegistryValueKind.DWord);
                    // PortableMode is NOT stored in the registry — if it's false
                    // (the normal case) we use the registry; if it's true we use
                    // the file. Storing false here would be redundant, and we never
                    // reach SaveToRegistry() when PortableMode is true anyway.
                }
            }
            catch { }
        }

        private static void SaveToFile()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# WgSharp settings (portable mode)");
                sb.AppendLine("PortableMode=" + (PortableMode ? "true" : "false"));
                sb.AppendLine("UseWireGuardNt=" + (UseWireGuardNt ? "true" : "false"));
                sb.AppendLine("DebugLog=" + (DebugLog ? "true" : "false"));
                sb.AppendLine("CheckForUpdates=" + (CheckForUpdates ? "true" : "false"));
                sb.AppendLine("StartGuiAtLogin=" + (StartGuiAtLogin ? "true" : "false"));
                sb.AppendLine("ServiceWasInstalled=" + (ServiceWasInstalled ? "true" : "false"));
                sb.AppendLine("TunnelOrder=" + (TunnelOrder ?? ""));
                sb.AppendLine("ThemeIsDark=" + (ThemeIsDark ? "true" : "false"));
                sb.AppendLine("ThemeOverrideSet=" + (ThemeOverrideSet ? "true" : "false"));
                sb.AppendLine("AutoConnectEnabled=" + (AutoConnectEnabled ? "true" : "false"));
                sb.AppendLine("AutoConnectTunnel=" + (AutoConnectTunnel ?? ""));
                sb.AppendLine("TrustedNetworks=" + NormalizeList(TrustedNetworks));
                sb.AppendLine("WiredIsTrusted=" + (WiredIsTrusted ? "true" : "false"));
                File.WriteAllText(FileSettingsPath, sb.ToString());
            }
            catch { }
        }

        // A multi-line value would corrupt the key=value file format.
        private static string NormalizeList(string v)
        {
            return (v ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        // ------------------------------------------------------------------ //
        //  Helpers                                                             //
        // ------------------------------------------------------------------ //

        private static bool ReadBool(RegistryKey k, string name, bool defaultVal)
        {
            object v = k.GetValue(name);
            if (v == null) return defaultVal;
            if (v is int) return (int)v != 0;
            return ParseBool(v.ToString());
        }

        private static string ReadString(RegistryKey k, string name, string defaultVal)
        {
            object v = k.GetValue(name);
            return v != null ? v.ToString() : defaultVal;
        }

        private static bool ParseBool(string v)
        {
            return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1";
        }
    }
}

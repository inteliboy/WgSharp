using System;
using System.IO;
using System.Reflection;

namespace WgSharp.Core
{
    /// <summary>
    /// Detects whether this process is running from WgSharp's fixed MSI
    /// install location (Program Files\WgSharp — see installer\Product.wxs,
    /// which deliberately offers no "choose a folder" dialog so this stays a
    /// reliable signal). When it is, WgSharp treats this as "I was properly
    /// installed, not just unzipped somewhere" and adjusts its defaults:
    ///
    ///   - Start GUI at login and the background service are auto-enabled on
    ///     first run (see MainForm's startup sequence).
    ///   - Portable mode is disallowed entirely, persistently, not just on
    ///     first run (see SettingsPanel) — portable mode is for the
    ///     standalone/zip distribution that travels with its own folder;
    ///     it doesn't make sense for a proper Program Files install.
    ///
    /// None of this applies to a copy run from anywhere else (a zip
    /// extraction, a USB stick, a dev build in this repo's bin\amd64) — those
    /// keep today's defaults (everything off, portable mode available)
    /// exactly as before.
    /// </summary>
    public static class InstallLocation
    {
        private static bool? _cached;

        /// <summary>The fixed path the MSI installs to, with no trailing slash.</summary>
        public static string ExpectedInstallDir
        {
            get
            {
                string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programFiles, "WgSharp");
            }
        }

        /// <summary>True if this process's exe lives directly in the fixed install directory.</summary>
        public static bool IsInstalled()
        {
            if (_cached.HasValue) return _cached.Value;
            try
            {
                string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                string expected = ExpectedInstallDir;
                // Trim trailing separators before comparing so "...\WgSharp" and
                // "...\WgSharp\" are treated the same.
                string a = (exeDir ?? "").TrimEnd('\\', '/');
                string b = expected.TrimEnd('\\', '/');
                _cached = string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
            }
            catch { _cached = false; }
            return _cached.Value;
        }

        /// <summary>
        /// Called once at startup (after AppSettings.Load(), before anything
        /// reads its values) from a genuine first run — no settings file on
        /// disk yet — when running from the fixed install location. Turns on
        /// "Start GUI at login" and installs (but doesn't start) the
        /// background service, matching what a person would otherwise have
        /// to go find in Settings and turn on by hand. Does nothing on a
        /// normal/later launch, and does nothing at all when not running from
        /// the install location — those keep today's defaults exactly as
        /// before. Best-effort throughout: a failure here (e.g. installing
        /// the service needs admin, which the app's manifest already
        /// requires, but just in case) never blocks the app from starting.
        /// </summary>
        public static void ApplyFirstRunDefaultsIfApplicable()
        {
            if (!IsInstalled()) return;
            if (AppSettings.SettingsFileExists) return; // not a first run; respect whatever the user has set since

            try
            {
                AppSettings.StartGuiAtLogin = true;
                AppSettings.PortableMode = false; // already the default; explicit for clarity
                try { LoginAutostart.Enable(); } catch { }
                try { if (!ServiceInstaller.IsInstalled()) ServiceInstaller.Install(); } catch { }
                AppSettings.Save();
            }
            catch { /* never block startup over this */ }
        }

        /// <summary>
        /// Unlike ApplyFirstRunDefaultsIfApplicable, this runs on EVERY
        /// launch from the install location, not just the first: Portable
        /// mode is meant for the standalone/zip distribution that travels
        /// with its own folder, and doesn't make sense for a proper Program
        /// Files install — so it's disallowed persistently here, not just
        /// defaulted off once. (SettingsPanel separately disables the
        /// checkbox itself so there's no UI path to turn it back on while
        /// running from this location; this is the belt-and-suspenders
        /// enforcement in case a settings file with PortableMode=true ever
        /// ends up next to an installed copy, e.g. copied in from elsewhere.)
        /// </summary>
        public static void EnforcePortableModeRestriction()
        {
            if (!IsInstalled()) return;
            if (!AppSettings.PortableMode) return;
            try
            {
                AppSettings.PortableMode = false;
                AppSettings.Save();
            }
            catch { }
        }
    }
}

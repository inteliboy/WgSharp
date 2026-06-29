using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace WgSharp.Tun
{
    /// <summary>
    /// Ensures the native driver DLLs are present next to the executable, fetched
    /// at application startup (not lazily on first connect). wintun.dll is always
    /// fetched (the managed backend needs it); wireguard.dll (WireGuardNT) is
    /// fetched too so the WireGuardNT backend is ready when selected. Runs on a
    /// background thread so the UI isn't blocked; progress is reported via Log.
    /// </summary>
    public static class DriverBootstrap
    {
        public static event Action<string> Log;
        // IMPORTANT: route through Logger.Tag, not a plain string concat. The
        // Wintun/WireGuardNT downloaders we subscribe to below already emit
        // messages that may carry a leading "[dbg] " marker (see Logger). A
        // plain "[Bootstrap] " + m prefix would push that marker into the
        // MIDDLE of the string ("[Bootstrap] [dbg] ..."), and Logger.ShouldShow
        // only recognizes the marker at the very start — so a debug-marked
        // downloader line would silently stop being filterable the moment it
        // passed through here. Logger.Tag keeps the marker (if any) in front.
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Bootstrap")); }

        private static int _started;

        public static string BaseDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        /// <summary>
        /// Kick off the downloads once, on a background thread. Safe to call
        /// multiple times; only the first call does work.
        /// </summary>
        public static void EnsureDriversAsync()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            var t = new Thread(Run) { IsBackground = true, Name = "wg-driver-bootstrap" };
            t.Start();
        }

        private static void Run()
        {
            string dir = BaseDir;

            // wintun.dll — required by the managed backend.
            try
            {
                if (!WintunDownloader.IsPresent(dir))
                {
                    WintunDownloader.Log += L;
                    L(WgSharp.Core.Logger.DebugMarker + "Fetching wintun.dll at startup\u2026");
                    WintunDownloader.EnsurePresent(dir);
                }
            }
            catch (Exception ex)
            {
                // A failure here is meaningful (not debug-only): it affects
                // whether the managed backend will work, so it always shows.
                L("Could not fetch wintun.dll at startup: " + ex.Message +
                  " (will retry on connect).");
            }

            // wireguard.dll — for the WireGuardNT backend. Best-effort; only needed
            // if/when that backend is selected.
            try
            {
                if (!WireGuardNtDownloader.IsPresent(dir))
                {
                    WireGuardNtDownloader.Log += L;
                    L(WgSharp.Core.Logger.DebugMarker + "Fetching wireguard.dll (WireGuardNT) at startup\u2026");
                    WireGuardNtDownloader.EnsurePresent(dir);
                }
            }
            catch (Exception ex)
            {
                L("Could not fetch wireguard.dll at startup: " + ex.Message +
                  " (the WireGuardNT backend will be unavailable until it's present).");
            }

            // Always shown (not debug-marked): a single, quiet confirmation that
            // startup driver bootstrap finished, without any of the granular
            // download/verification detail above.
            L("Driver bootstrap complete.");
        }
    }
}

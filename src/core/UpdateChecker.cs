using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;

namespace WgSharp.Core
{
    /// <summary>
    /// Checks GitHub for a newer WgSharp release and, if one exists, hands the
    /// caller the new version and the page to open. Everything here is
    /// best-effort and completely non-fatal: an update check must never disrupt
    /// the app, so every failure path (offline, rate-limited, API change,
    /// unparseable version) simply reports "no update" and moves on.
    ///
    /// Versions are date-based: the exe is stamped 1.YY.MMDD.0 (build.cmd) and
    /// releases are tagged 1.YY.MMDD (no 4th field). "Newer" is a field-by-field
    /// numeric comparison of major.YY.MMDD, so 1.26.0704 &gt; 1.26.0629 &gt;
    /// 1.25.1230, independent of string length quirks.
    /// </summary>
    public static class UpdateChecker
    {
        public const string ReleasesPage = "https://github.com/inteliboy/WgSharp/releases";
        private const string LatestApi = "https://api.github.com/repos/inteliboy/WgSharp/releases/latest";
        private const string TagUrlPrefix = "https://github.com/inteliboy/WgSharp/releases/tag/";

        // The asset name build.cmd's NSIS step produces (installer\WgSharp.nsi's
        // OutFile) and that every published release is expected to attach under
        // this exact name - lets a caller offer "download and install" directly
        // instead of just opening the release page and making the user find and
        // run it themselves.
        public const string InstallerAssetName = "WgSharp-Setup.exe";

        /// <summary>The outcome of a check. IsUpdateAvailable is the only field that matters to most callers.</summary>
        public sealed class Result
        {
            public bool IsUpdateAvailable;
            public string CurrentVersion;   // e.g. "1.26.0629"
            public string LatestVersion;    // e.g. "1.26.0704", or null on failure/no-newer
            public string ReleaseUrl;       // tag page for the latest, else the releases index
            public string InstallerDownloadUrl; // direct download URL for InstallerAssetName, or null if the release has no such asset
        }

        /// <summary>
        /// Runs the check on a background thread and invokes <paramref name="onResult"/>
        /// with the outcome. onResult is called on the thread-pool thread, so a
        /// WinForms caller should marshal back to the UI thread itself. Never
        /// throws. If the newest release isn't newer than the running build (or
        /// anything fails), onResult still fires with IsUpdateAvailable=false so
        /// the caller can update any "last checked" state without special-casing.
        /// </summary>
        public static void CheckAsync(Action<Result> onResult)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                Result r = Check();
                try { if (onResult != null) onResult(r); }
                catch { /* caller's callback isn't our problem */ }
            });
        }

        /// <summary>Synchronous check (used by CheckAsync). Never throws.</summary>
        public static Result Check()
        {
            var r = new Result();
            r.CurrentVersion = CurrentVersion();
            r.ReleaseUrl = ReleasesPage;

            try
            {
                // GitHub requires TLS 1.2+ and a User-Agent; without the UA it
                // returns 403. Same TLS bump the driver downloaders use.
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }

                string json;
                using (var client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "WgSharp/" + (r.CurrentVersion ?? "0"));
                    client.Headers.Add("Accept", "application/vnd.github+json");
                    json = client.DownloadString(LatestApi);
                }

                string latestTag = ExtractTagName(json);
                if (string.IsNullOrEmpty(latestTag)) return r; // couldn't parse; no update

                r.LatestVersion = latestTag;
                r.ReleaseUrl = TagUrlPrefix + latestTag;
                r.InstallerDownloadUrl = ExtractAssetDownloadUrl(json, InstallerAssetName);

                if (CompareVersions(latestTag, r.CurrentVersion) > 0)
                    r.IsUpdateAvailable = true;
            }
            catch
            {
                // Offline, rate-limited, DNS failure, API shape change, etc.
                // Silently report "no update".
                r.IsUpdateAvailable = false;
            }
            return r;
        }

        /// <summary>
        /// The running build's version, e.g. "1.26.0705", normalized from the
        /// exe's assembly version to line up with the GitHub release tag
        /// format. Falls back to the assembly version, then "0".
        /// </summary>
        public static string CurrentVersion()
        {
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
                if (attrs.Length > 0)
                {
                    string v = ((AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion;
                    v = NormalizeVersion(v);
                    if (!string.IsNullOrEmpty(v)) return v;
                }
            }
            catch { }
            try
            {
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                if (v != null) return NormalizeVersion(v.ToString());
            }
            catch { }
            return "0";
        }

        // Trims to the numeric version core, keeping up to FOUR dotted fields
        // (major.YY.MMDD and an optional same-day revision), and drops any
        // suffix (e.g. "1.26.0704-rc" -> "1.26.0704", "1.26.0704.1-beta" ->
        // "1.26.0704.1"). Four fields matter because releases are normally
        // tagged major.YY.MMDD, but a second build on the same day is tagged
        // major.YY.MMDD.N so it still compares as newer. Returns "" if nothing
        // numeric is found.
        // Trims to the numeric major.minor.build core and drops any 4th field
        // and suffix (e.g. "1.26.0705.0" -> "1.26.0705", "1.26.0705-rc" ->
        // "1.26.0705"), lining the exe's assembly version up with the plain
        // 1.YY.MMDD release tag. Returns "" if nothing numeric is found.
        private static string NormalizeVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            Match m = Regex.Match(v, @"\d+(?:\.\d+){1,3}");
            if (!m.Success) return "";
            string[] parts = m.Value.Split('.');
            int take = Math.Min(3, parts.Length);
            return string.Join(".", parts, 0, take);
        }

        // Pulls "tag_name":"1.26.0704" out of the releases/latest JSON without a
        // JSON library (keeping this dependency-free on .NET Framework), then
        // normalizes it. Tolerant of surrounding whitespace and a leading "v".
        private static string ExtractTagName(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            Match m = Regex.Match(json, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
            if (!m.Success) return null;
            return NormalizeVersion(m.Groups[1].Value);
        }

        // Pulls the browser_download_url for one named asset out of the
        // releases/latest JSON's "assets" array, again without a JSON library.
        // GitHub's API always emits an asset's "name" field before its own
        // "browser_download_url" within that same (flat, no nested {}) asset
        // object, so a bounded non-greedy scan forward from the name match -
        // stopping at the next "{"/"}" - reliably stays within the matched
        // asset's own object instead of spilling into a neighboring one.
        private static string ExtractAssetDownloadUrl(string json, string assetName)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pattern = "\"name\"\\s*:\\s*\"" + Regex.Escape(assetName) +
                              "\"[^{}]*?\"browser_download_url\"\\s*:\\s*\"([^\"]+)\"";
            Match m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Downloads the installer asset to a temp file on a background thread
        /// and reports the result. onComplete gets (localPath, null) on success
        /// or (null, exception) on failure; it runs on the thread-pool thread,
        /// same as CheckAsync's callback, so a WinForms caller must marshal back
        /// to the UI thread itself. Never throws.
        /// </summary>
        public static void DownloadInstallerAsync(string url, Action<string, Exception> onComplete)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string path = null;
                Exception error = null;
                try
                {
                    try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
                    path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), InstallerAssetName);
                    using (var client = new WebClient())
                    {
                        client.Headers.Add("User-Agent", "WgSharp/" + CurrentVersion());
                        client.DownloadFile(url, path);
                    }
                }
                catch (Exception ex) { error = ex; path = null; }
                try { if (onComplete != null) onComplete(path, error); }
                catch { /* caller's callback isn't our problem */ }
            });
        }

        /// <summary>
        /// Launches a downloaded WgSharp-Setup.exe silently ("/S" — no wizard,
        /// no Finish page, just install-and-relaunch; see installer\WgSharp.nsi).
        /// UseShellExecute=true is required, not just the default: Setup's own
        /// manifest requests requireAdministrator, and ShellExecute is what
        /// makes Windows honor that and show the UAC prompt for the CHILD
        /// process regardless of this (asInvoker) process's own elevation
        /// level — a raw CreateProcess (UseShellExecute=false) would instead
        /// just fail outright. Throws on failure so the caller can fall back
        /// to opening the release page.
        /// </summary>
        public static void LaunchInstallerSilently(string installerPath)
        {
            var psi = new ProcessStartInfo(installerPath, "/S");
            psi.UseShellExecute = true;
            Process.Start(psi);
        }

        /// <summary>
        /// Field-by-field numeric compare of two dotted versions. Missing fields
        /// count as 0. Returns &gt;0 if a is newer than b, &lt;0 if older, 0 if
        /// equal. Non-numeric or empty inputs are treated as 0.0.0 so a garbage
        /// tag never falsely claims to be an update.
        /// </summary>
        public static int CompareVersions(string a, string b)
        {
            int[] pa = ToParts(a), pb = ToParts(b);
            int n = Math.Max(pa.Length, pb.Length);
            for (int i = 0; i < n; i++)
            {
                int x = i < pa.Length ? pa[i] : 0;
                int y = i < pb.Length ? pb[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }
            return 0;
        }

        private static int[] ToParts(string v)
        {
            if (string.IsNullOrEmpty(v)) return new int[0];
            string[] raw = v.Split('.');
            int[] parts = new int[raw.Length];
            for (int i = 0; i < raw.Length; i++)
            {
                int val;
                // int.TryParse guards against non-numeric fields (e.g. a "-rc"
                // that slipped through), treating them as 0.
                parts[i] = int.TryParse(raw[i].Trim(), out val) ? val : 0;
            }
            return parts;
        }
    }
}

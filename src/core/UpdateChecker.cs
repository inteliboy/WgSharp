using System;
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

        /// <summary>The outcome of a check. IsUpdateAvailable is the only field that matters to callers.</summary>
        public sealed class Result
        {
            public bool IsUpdateAvailable;
            public string CurrentVersion;   // e.g. "1.26.0629"
            public string LatestVersion;    // e.g. "1.26.0704", or null on failure/no-newer
            public string ReleaseUrl;       // tag page for the latest, else the releases index
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

                int cmp = CompareVersions(latestTag, r.CurrentVersion);
                if (cmp > 0)
                {
                    // Release version is clearly newer.
                    r.IsUpdateAvailable = true;
                }
                else if (cmp == 0)
                {
                    // Same version. This is the same-day re-release case: the
                    // tag wasn't bumped, so versions can't distinguish them.
                    // Fall back to comparing timestamps — the release's newest
                    // asset upload vs this build's stamped time. Only counts if
                    // the asset is meaningfully newer (a margin absorbs
                    // build-machine vs GitHub clock skew and avoids nagging on a
                    // release that was merely edited around the same time).
                    DateTime assetTime, buildTime;
                    if (TryGetLatestAssetTime(json, out assetTime) &&
                        TryGetBuildTimestamp(out buildTime))
                    {
                        if (assetTime.ToUniversalTime() - buildTime.ToUniversalTime() > TimestampMargin)
                            r.IsUpdateAvailable = true;
                    }
                    // No usable timestamps -> treat equal versions as up to date
                    // (the old behavior), never a false prompt.
                }
                // cmp < 0: running build is newer than the release; no update.
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
        /// How much newer the release's asset must be than this build before the
        /// same-version tiebreaker counts it as an update. Absorbs clock skew
        /// between the build machine and GitHub, and avoids nagging when a
        /// release is edited/re-saved around the same time as the build.
        /// </summary>
        private static readonly TimeSpan TimestampMargin = TimeSpan.FromMinutes(5);

        /// <summary>
        /// Reads the build timestamp stamped into the exe at build time
        /// (AssemblyMetadata "BuildTimestamp", ISO-8601 UTC). Returns false if
        /// absent or unparseable (e.g. a dev build with no stamp) — the caller
        /// then simply skips the timestamp tiebreaker.
        /// </summary>
        public static bool TryGetBuildTimestamp(out DateTime utc)
        {
            utc = DateTime.MinValue;
            try
            {
                object[] attrs = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
                foreach (object a in attrs)
                {
                    var m = a as AssemblyMetadataAttribute;
                    if (m != null && m.Key == "BuildTimestamp" && !string.IsNullOrEmpty(m.Value))
                    {
                        return DateTime.TryParse(m.Value,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AdjustToUniversal |
                            System.Globalization.DateTimeStyles.AssumeUniversal, out utc);
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Finds the most recent asset upload time in the releases/latest JSON
        /// by scanning every "updated_at" that appears inside the "assets"
        /// array and taking the max. Falling back to the release-level
        /// "published_at" would nag on note-only edits, so we deliberately use
        /// only the asset timestamps (a re-uploaded binary is what should
        /// trigger this). Returns false if there are no assets/timestamps.
        /// </summary>
        public static bool TryGetLatestAssetTime(string json, out DateTime utc)
        {
            utc = DateTime.MinValue;
            if (string.IsNullOrEmpty(json)) return false;

            // Narrow to the "assets": [ ... ] array so we don't accidentally
            // pick up the release-level published_at/created_at/updated_at.
            int ai = json.IndexOf("\"assets\"", StringComparison.Ordinal);
            if (ai < 0) return false;
            int lb = json.IndexOf('[', ai);
            if (lb < 0) return false;
            int depth = 0, end = -1;
            for (int i = lb; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '[') depth++;
                else if (c == ']') { depth--; if (depth == 0) { end = i; break; } }
            }
            if (end < 0) end = json.Length;
            string assets = json.Substring(lb, end - lb);

            bool found = false;
            foreach (Match m in Regex.Matches(assets, "\"updated_at\"\\s*:\\s*\"([^\"]+)\""))
            {
                DateTime t;
                if (DateTime.TryParse(m.Groups[1].Value,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal |
                        System.Globalization.DateTimeStyles.AssumeUniversal, out t))
                {
                    if (!found || t > utc) { utc = t; found = true; }
                }
            }
            return found;
        }

        /// <summary>
        /// The running build's version as major.YY.MMDD.REV (kept to 4 fields
        /// so a same-day revision tag like 1.YY.MMDD.1 compares as newer than
        /// the assembly version stripped so it lines up with the release tag
        /// format). Falls back to the assembly version, then "0".
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
        private static string NormalizeVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            Match m = Regex.Match(v, @"\d+(?:\.\d+){1,3}");
            if (!m.Success) return "";
            string[] parts = m.Value.Split('.');
            int take = Math.Min(4, parts.Length);
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

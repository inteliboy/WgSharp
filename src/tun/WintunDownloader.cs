using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;

namespace WgSharp.Tun
{
    /// <summary>
    /// Downloads the official signed wintun.dll from wintun.net and extracts the
    /// architecture-appropriate build next to the executable. The zip lays out
    /// DLLs as bin/&lt;arch&gt;/wintun.dll for amd64, arm64, arm, and x86.
    ///
    /// The version and its published SHA-256 are pinned below; bump both together
    /// when a new release ships (check https://www.wintun.net).
    /// </summary>
    public static class WintunDownloader
    {
        public const string Version = "0.14.1";
        public const string ZipUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip";
        // SHA-256 of wintun-0.14.1.zip as published on wintun.net.
        public const string ZipSha256 = "07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51";

        public static event Action<string> Log;
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Wintun")); }

        /// <summary>The bin/ subfolder name for the current process architecture.</summary>
        public static string ArchFolder()
        {
            // We only support amd64; this is belt-and-suspenders in case the
            // downloader is ever called on a 32-bit process (it shouldn't be).
            return IntPtr.Size == 8 ? "amd64" : "x86";
        }

        /// <summary>
        /// Reads the PE Machine field from a DLL and returns the matching
        /// arch folder name ("amd64", "arm64", "x86"), or null on any error.
        /// The PE Machine field is at a fixed offset: the DOS stub's e_lfanew
        /// (4-byte offset at 0x3C) points to the PE signature, and the Machine
        /// WORD follows 4 bytes after that (after the "PE\0\0" signature).
        /// </summary>
        private static string PeMachineFolder(string dllPath)
        {
            try
            {
                using (var fs = File.OpenRead(dllPath))
                using (var br = new System.IO.BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return null;
                    // DOS header: MZ magic at 0, e_lfanew at 0x3C.
                    fs.Seek(0x3C, SeekOrigin.Begin);
                    int peOffset = br.ReadInt32();
                    if (peOffset <= 0 || peOffset + 6 > fs.Length) return null;
                    // PE signature ("PE\0\0") + Machine WORD.
                    fs.Seek(peOffset, SeekOrigin.Begin);
                    uint sig = br.ReadUInt32();
                    if (sig != 0x00004550) return null; // not "PE\0\0"
                    ushort machine = br.ReadUInt16();
                    if (machine == 0xAA64) return "arm64";
                    if (machine == 0x8664) return "amd64";
                    if (machine == 0x014c) return "x86";
                    return null; // unknown
                }
            }
            catch { return null; }
        }

        public static bool IsPresent(string targetDir)
        {
            return File.Exists(Path.Combine(targetDir, "wintun.dll"));
        }

        /// <summary>
        /// Ensure wintun.dll exists in targetDir; download and extract if missing.
        /// Returns true if the DLL is present afterward. Throws on download/IO
        /// errors so the caller can show a precise message.
        /// </summary>
        public static bool EnsurePresent(string targetDir)
        {
            string dllPath = Path.Combine(targetDir, "wintun.dll");
            if (File.Exists(dllPath)) { L(WgSharp.Core.Logger.DebugMarker + "wintun.dll already present."); return true; }

            string arch = ArchFolder();
            L(WgSharp.Core.Logger.DebugMarker + "Architecture detected: " + arch + ". Downloading Wintun " + Version + "\u2026");

            string tmpZip = Path.Combine(Path.GetTempPath(), "wintun-" + Version + "-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Download(ZipUrl, tmpZip);
                VerifyHash(tmpZip, ZipSha256);
                L(WgSharp.Core.Logger.DebugMarker + "Download verified (SHA-256 OK). Extracting bin/" + arch + "/wintun.dll\u2026");
                ExtractDll(tmpZip, arch, dllPath);
                L(WgSharp.Core.Logger.DebugMarker + "wintun.dll installed to " + dllPath);
                return File.Exists(dllPath);
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        private static void Download(string url, string destPath)
        {
            // wintun.net is HTTPS; force TLS 1.2 since .NET 4.8 may default lower
            // on older OS configurations.
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }

            using (var client = new WebClient())
            {
                client.Headers.Add("User-Agent", "WgSharp/" + Version);
                client.DownloadFile(url, destPath);
            }
        }

        private static void VerifyHash(string path, string expectedHexLower)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(fs);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                string actual = sb.ToString();
                if (!string.Equals(actual, expectedHexLower, StringComparison.OrdinalIgnoreCase))
                    throw new Exception("Downloaded Wintun zip failed integrity check.\nExpected " +
                                        expectedHexLower + "\nActual   " + actual);
            }
        }

        private static void ExtractDll(string zipPath, string arch, string dllDest)
        {
            // The zip lays the DLLs out under wintun/bin/<arch>/wintun.dll.
            string entryName = "wintun/bin/" + arch + "/wintun.dll";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry entry = null;
                foreach (var e in zip.Entries)
                {
                    // match case-insensitively, tolerate backslashes, and also
                    // accept a layout without the leading wintun/ folder just in case.
                    string normalized = e.FullName.Replace('\\', '/');
                    if (string.Equals(normalized, entryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalized, "bin/" + arch + "/wintun.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        entry = e;
                        break;
                    }
                }
                if (entry == null)
                    throw new Exception("Could not find bin/" + arch + "/wintun.dll inside the Wintun zip.");

                entry.ExtractToFile(dllDest, true);
            }
        }
    }
}

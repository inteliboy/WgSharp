using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WgSharp.Core;

namespace WgSharp.Tun
{
    /// <summary>
    /// Downloads the official signed wireguard.dll (WireGuardNT) and extracts the
    /// architecture-appropriate build next to the executable. The zip lays out
    /// DLLs as wireguard-nt/bin/&lt;arch&gt;/wireguard.dll.
    ///
    /// WireGuardNT is the kernel-mode WireGuard implementation; loading this DLL
    /// lets the app drive the in-kernel data plane via the WireGuardNtTunnel
    /// backend, for far higher throughput than the managed tunnel. It is optional —
    /// only needed when the WireGuardNT backend is selected.
    ///
    /// Integrity is verified in two complementary ways:
    ///   1. If ZipSha256 is set (lowercase hex), the downloaded zip's SHA-256 is
    ///      checked against it. WireGuard's download server doesn't publish a
    ///      sidecar checksum file for this zip, so this stays empty by default —
    ///      set it to a hash you've confirmed for the exact release you ship to
    ///      pin it.
    ///   2. Regardless of the zip hash, the EXTRACTED wireguard.dll's Authenticode
    ///      signature is verified: the embedded code signature must be valid (a
    ///      trusted, unbroken cert chain) AND the signing certificate's subject
    ///      must name WireGuard LLC. This catches a tampered or substituted DLL
    ///      even without a pinned zip hash, and is the stronger check since the
    ///      DLL itself is what gets loaded into the process.
    /// </summary>
    public static class WireGuardNtDownloader
    {
        public const string Version = "1.1";
        public const string ZipUrl = "https://download.wireguard.com/wireguard-nt/wireguard-nt-1.1.zip";
        // Set this to the published SHA-256 (lowercase hex) of wireguard-nt-1.1.zip
        // to enable integrity verification. Left empty until confirmed.
        public const string ZipSha256 = "";

        public static event Action<string> Log;
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "WireGuardNT")); }

        public static string ArchFolder()
        {
            // Reuse the same arch-detection logic as Wintun (process bitness/arch).
            return WintunDownloader.ArchFolder();
        }

        public static bool IsPresent(string targetDir)
        {
            return File.Exists(Path.Combine(targetDir, "wireguard.dll"));
        }

        /// <summary>
        /// Ensure wireguard.dll exists in targetDir; download and extract if
        /// missing. Returns true if present afterward. Throws on download/IO errors.
        /// </summary>
        public static bool EnsurePresent(string targetDir)
        {
            string dllPath = Path.Combine(targetDir, "wireguard.dll");
            if (File.Exists(dllPath)) { L(WgSharp.Core.Logger.DebugMarker + "wireguard.dll already present."); return true; }

            string arch = ArchFolder();
            L(WgSharp.Core.Logger.DebugMarker + "Downloading WireGuardNT " + Version + " (" + arch + ")\u2026");

            string tmpZip = Path.Combine(Path.GetTempPath(),
                "wireguard-nt-" + Version + "-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                Download(ZipUrl, tmpZip);
                if (!string.IsNullOrEmpty(ZipSha256))
                {
                    VerifyHash(tmpZip, ZipSha256);
                    L(WgSharp.Core.Logger.DebugMarker + "WireGuardNT download verified (SHA-256 OK). Extracting wireguard.dll\u2026");
                }
                else
                {
                    L(WgSharp.Core.Logger.DebugMarker + "WireGuardNT download complete (no pinned hash to verify). Extracting wireguard.dll\u2026");
                }
                ExtractDll(tmpZip, arch, dllPath);
                L(WgSharp.Core.Logger.DebugMarker + "wireguard.dll installed to " + dllPath);

                // Verify the EXTRACTED DLL's Authenticode signature — the real
                // integrity gate, since this DLL is what gets loaded. Throws if
                // the signature is missing/invalid or not from WireGuard LLC.
                VerifyAuthenticode(dllPath);
                L(Logger.DebugMarker + "wireguard.dll signature verified (WireGuard LLC, valid chain).");
                return File.Exists(dllPath);
            }
            finally
            {
                try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
            }
        }

        private static void Download(string url, string destPath)
        {
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
                    throw new Exception("Downloaded WireGuardNT zip failed integrity check.\nExpected " +
                                        expectedHexLower + "\nActual   " + actual);
            }
        }

        private static void ExtractDll(string zipPath, string arch, string dllDest)
        {
            // The zip lays the DLLs out under wireguard-nt/bin/<arch>/wireguard.dll.
            string entryName = "wireguard-nt/bin/" + arch + "/wireguard.dll";
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry entry = null;
                foreach (var e in zip.Entries)
                {
                    string normalized = e.FullName.Replace('\\', '/');
                    if (string.Equals(normalized, entryName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(normalized, "bin/" + arch + "/wireguard.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        entry = e;
                        break;
                    }
                }
                if (entry == null)
                    throw new Exception("Could not find bin/" + arch + "/wireguard.dll inside the WireGuardNT zip.");
                entry.ExtractToFile(dllDest, true);
            }
        }

        // ---- Authenticode signature verification ----

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(IntPtr hwnd, [In] ref Guid pgActionID, [In] IntPtr pWVTData);

        // WINTRUST_ACTION_GENERIC_VERIFY_V2
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WTD_UI_NONE = 2;
        private const uint WTD_REVOKE_NONE = 0;
        private const uint WTD_REVOKE_WHOLECHAIN = 1;   // check revocation for the entire chain
        private const uint WTD_CHOICE_FILE = 1;
        private const uint WTD_STATEACTION_VERIFY = 1;
        private const uint WTD_STATEACTION_CLOSE = 2;

        // Revocation results that mean "the chain is otherwise valid but the
        // revocation status could not be determined" — typically an offline
        // machine or an unreachable OCSP/CRL endpoint. We accept these (the
        // signature and trust chain still verified) rather than hard-failing an
        // install behind a captive portal. A *definitive* revocation returns a
        // different code (CERT_E_REVOKED, 0x800B010C) and is rejected below.
        private const uint CERT_E_REVOCATION_FAILURE = 0x800B010E; // unable to check
        private const uint CRYPT_E_REVOCATION_OFFLINE = 0x80092013; // server offline
        private const uint CRYPT_E_NO_REVOCATION_CHECK = 0x80092012; // no revocation performed

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_FILE_INFO
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;     // pointer to WINTRUST_FILE_INFO
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        /// <summary>
        /// Verifies the file is Authenticode-signed with a valid, trusted chain
        /// AND that the signing certificate's subject names WireGuard. Throws
        /// with a clear message on any failure. This is the real integrity gate
        /// for the kernel-driver DLL we're about to load.
        /// </summary>
        private static void VerifyAuthenticode(string filePath)
        {
            // 1) Chain/trust validation via WinVerifyTrust (Authenticode policy).
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
                pcwszFilePath = filePath,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)));
            try
            {
                Marshal.StructureToPtr(fileInfo, pFile, false);
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pFile = pFile,
                    dwStateAction = WTD_STATEACTION_VERIFY
                };
                IntPtr pData = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_DATA)));
                try
                {
                    Marshal.StructureToPtr(data, pData, false);
                    Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                    uint result = WinVerifyTrust(IntPtr.Zero, ref action, pData);

                    // Always close the state, regardless of the verify result.
                    var closeData = (WINTRUST_DATA)Marshal.PtrToStructure(pData, typeof(WINTRUST_DATA));
                    closeData.dwStateAction = WTD_STATEACTION_CLOSE;
                    Marshal.StructureToPtr(closeData, pData, false);
                    WinVerifyTrust(IntPtr.Zero, ref action, pData);

                    if (result != 0)
                    {
                        if (result == CERT_E_REVOCATION_FAILURE ||
                            result == CRYPT_E_REVOCATION_OFFLINE ||
                            result == CRYPT_E_NO_REVOCATION_CHECK)
                        {
                            // Chain and signature verified; only the revocation
                            // status was indeterminate (offline / unreachable
                            // CRL/OCSP). Accept, but say so — a revoked cert
                            // returns CERT_E_REVOKED and is NOT swallowed here.
                            L(Logger.DebugMarker + "wireguard.dll signature valid; revocation status " +
                              "could not be checked (offline?) — 0x" + result.ToString("X8") + ".");
                        }
                        else
                        {
                            throw new Exception("wireguard.dll Authenticode signature is invalid, untrusted, " +
                                                "or revoked (WinVerifyTrust 0x" + result.ToString("X8") + ").");
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(pData); }
            }
            finally { Marshal.FreeHGlobal(pFile); }

            // 2) Publisher check: the signing cert's Organization (O=) must be
            // exactly "WireGuard LLC". This is deliberately stricter than a bare
            // substring search for "WireGuard" anywhere in the DN: a substring
            // test would accept a cert whose CN/OU merely *contained* the word
            // (e.g. "Not WireGuard Inc"), as long as it chained to a trusted
            // root. Matching the O relative-distinguished-name to the known
            // signer name is the meaningful publisher assertion.
            string subject = null;
            try
            {
                var cert = X509Certificate.CreateFromSignedFile(filePath);
                subject = cert.Subject ?? "";
            }
            catch (Exception ex)
            {
                throw new Exception("Could not confirm wireguard.dll's signing publisher: " + ex.Message);
            }

            string org = ExtractRdn(subject, "O");
            if (!string.Equals(org, "WireGuard LLC", StringComparison.OrdinalIgnoreCase))
                throw new Exception("wireguard.dll is signed, but not by WireGuard LLC (cert subject: " +
                                    subject + ").");
        }

        /// <summary>
        /// Extracts a single relative-distinguished-name value (e.g. the "O"
        /// organization field) from an X.500 subject/issuer string as produced
        /// by X509Certificate.Subject. Handles the common
        /// "CN=..., O=WireGuard LLC, L=..., C=US" form, optional spaces after
        /// commas, and double-quoted values that themselves contain commas.
        /// Returns null if the RDN isn't present.
        /// </summary>
        private static string ExtractRdn(string dn, string rdn)
        {
            if (string.IsNullOrEmpty(dn)) return null;
            int i = 0;
            int n = dn.Length;
            while (i < n)
            {
                // Read the attribute type up to '='.
                int eq = dn.IndexOf('=', i);
                if (eq < 0) break;
                string type = dn.Substring(i, eq - i).Trim();
                int v = eq + 1;

                string value;
                if (v < n && dn[v] == '"')
                {
                    // Quoted value: read to the closing quote.
                    int end = dn.IndexOf('"', v + 1);
                    if (end < 0) { value = dn.Substring(v + 1); i = n; }
                    else { value = dn.Substring(v + 1, end - v - 1); i = end + 1; while (i < n && dn[i] != ',') i++; if (i < n) i++; }
                }
                else
                {
                    int comma = dn.IndexOf(',', v);
                    if (comma < 0) { value = dn.Substring(v); i = n; }
                    else { value = dn.Substring(v, comma - v); i = comma + 1; }
                }

                if (string.Equals(type, rdn, StringComparison.OrdinalIgnoreCase))
                    return value.Trim();
            }
            return null;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace WgSharp.Core
{
    /// <summary>
    /// Persists tunnel configurations. Two modes:
    ///
    ///  * Normal (default): DPAPI-encrypted files in C:\ProgramData\WgSharp\conf
    ///    as &lt;name&gt;.conf.dpapi, machine-scoped so the elevated app can read them
    ///    regardless of which user imported them. NOT portable across machines.
    ///
    ///  * Portable: password-encrypted files in a "conf" folder next to the
    ///    executable as &lt;name&gt;.conf.wgsp, using PortableCrypto. These travel with
    ///    the app folder and can be moved to another machine; the password is
    ///    required to create, import, or activate them.
    ///
    /// The mode is chosen from AppSettings.PortableMode.
    /// </summary>
    public static class ConfigStore
    {
        private static readonly byte[] Entropy = Encoding.ASCII.GetBytes("WgSharp.v1.config");
        private const string DpapiExt = ".conf.dpapi";
        private const string PortExt = ".conf.wgsp";

        private static bool Portable { get { return AppSettings.PortableMode; } }

        private static string ExeDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        public static string StoreDir
        {
            get
            {
                if (Portable)
                    return Path.Combine(ExeDir, "conf");
                string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(Path.Combine(programData, "WgSharp"), "conf");
            }
        }

        public static void EnsureDir() { Directory.CreateDirectory(StoreDir); }

        private static string Ext { get { return Portable ? PortExt : DpapiExt; } }

        private static string PathFor(string name)
        {
            return Path.Combine(StoreDir, SanitizeName(name) + Ext);
        }

        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "tunnel";
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == ' ')
                    sb.Append(c);
            }
            string s = sb.ToString().Trim();
            return s.Length == 0 ? "tunnel" : s;
        }

        /// <summary>True if the current mode needs a password for save/load.</summary>
        public static bool RequiresPassword { get { return Portable; } }

        /// <summary>
        /// Save a config. In portable mode a non-empty password is required and the
        /// file is password-encrypted; otherwise DPAPI is used and password is
        /// ignored.
        /// </summary>
        public static void Save(string name, string configText, string password)
        {
            EnsureDir();
            if (Portable)
            {
                if (string.IsNullOrEmpty(password))
                    throw new InvalidOperationException("A password is required in portable mode.");
                byte[] blob = PortableCrypto.Encrypt(configText, password);
                File.WriteAllBytes(PathFor(name), blob);
            }
            else
            {
                byte[] plain = Encoding.UTF8.GetBytes(configText);
                byte[] enc = ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
                File.WriteAllBytes(PathFor(name), enc);
                Array.Clear(plain, 0, plain.Length);
            }
        }

        // Backwards-compatible overload for non-portable callers.
        public static void Save(string name, string configText)
        {
            Save(name, configText, null);
        }

        /// <summary>
        /// Load a config. In portable mode a password is required; otherwise DPAPI
        /// is used.
        /// </summary>
        public static string Load(string name, string password)
        {
            byte[] data = File.ReadAllBytes(PathFor(name));
            if (Portable)
            {
                return PortableCrypto.Decrypt(data, password);
            }
            byte[] plain = ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);
            string text = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            return text;
        }

        public static string Load(string name)
        {
            return Load(name, null);
        }

        public static bool Exists(string name)
        {
            return File.Exists(PathFor(name));
        }

        public static void Delete(string name)
        {
            string p = PathFor(name);
            if (File.Exists(p)) File.Delete(p);
        }

        public static List<string> List()
        {
            var result = new List<string>();
            if (!Directory.Exists(StoreDir)) return result;
            string ext = Ext;
            foreach (string file in Directory.GetFiles(StoreDir, "*" + ext))
            {
                string fn = Path.GetFileName(file);
                if (fn.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    result.Add(fn.Substring(0, fn.Length - ext.Length));
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }
    }
}

using System;
using System.IO;

namespace WgSharp.Core
{
    /// <summary>
    /// Config persistence for the unelevated GUI. Writes go directly to disk
    /// when possible and fall back to the background service when not:
    ///
    ///  * Portable mode: always direct — the portable store lives next to the
    ///    exe by design (a writable folder; portable mode is disallowed for
    ///    Program Files installs), and only the GUI knows the password.
    ///
    ///  * Machine store (ProgramData): try the direct write first. It
    ///    succeeds for files this user created (CREATOR OWNER) and for fresh
    ///    installs. It fails with access-denied for files created by OLDER,
    ///    ELEVATED versions of WgSharp — those are owned by Administrators —
    ///    in which case the write is routed through the service
    ///    (SAVECONFIG/DELETECONFIG, admin-gated, performed as SYSTEM), which
    ///    handles them regardless of ownership.
    ///
    /// This keeps ProgramData's default ACLs untouched: loosening them so
    /// every user could modify configs would let a standard user tamper with
    /// a tunnel an administrator later activates.
    /// </summary>
    public static class ConfigWriter
    {
        public static void Save(string name, string configText)
        {
            Save(name, configText, null);
        }

        public static void Save(string name, string configText, string password)
        {
            if (AppSettings.PortableMode || !string.IsNullOrEmpty(password))
            {
                ConfigStore.Save(name, configText, password);
                return;
            }

            Exception direct;
            try { ConfigStore.Save(name, configText); return; }
            catch (UnauthorizedAccessException ex) { direct = ex; }
            catch (IOException ex) { direct = ex; }

            string err;
            string resp = ServiceClient.SendCommand(
                "SAVECONFIG|" + ConfigStore.SanitizeName(name) + "|" + ServiceProtocol.Escape(configText), out err);
            if (resp == "OK") return;
            throw new Exception(BuildFailure("save", name, direct, resp, err));
        }

        public static void Delete(string name)
        {
            if (AppSettings.PortableMode)
            {
                ConfigStore.Delete(name);
                return;
            }

            Exception direct;
            try { ConfigStore.Delete(name); return; }
            catch (UnauthorizedAccessException ex) { direct = ex; }
            catch (IOException ex) { direct = ex; }

            string err;
            string resp = ServiceClient.SendCommand(
                "DELETECONFIG|" + ConfigStore.SanitizeName(name), out err);
            if (resp == "OK") return;
            throw new Exception(BuildFailure("delete", name, direct, resp, err));
        }

        private static string BuildFailure(string what, string name, Exception direct, string resp, string connectErr)
        {
            string via;
            if (resp == null) via = "and the background service isn't reachable (" + (connectErr ?? "no response") + ")";
            else if (resp == "DENIED") via = "and the background service refused (your account must be in the Administrators group)";
            else if (resp.StartsWith("ERR|", StringComparison.Ordinal)) via = "and the background service reported: " + ServiceProtocol.Unescape(resp.Substring(4));
            else via = "and the background service replied unexpectedly: " + resp;
            return "Could not " + what + " tunnel '" + name + "': direct write failed (" +
                   direct.Message + ") " + via + ".";
        }
    }
}

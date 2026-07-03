using System;

namespace WgSharp.Core
{
    /// <summary>
    /// The wire format for the named pipe between the GUI and the background
    /// service. Deliberately tiny and text-based: one line in, one line out.
    ///
    /// The service is a long-running MANAGER (installed start=auto, idle when
    /// no tunnel is up), and activation/deactivation ARE pipe commands now —
    /// but with the lesson of the earlier broken design baked in: a control
    /// command only VALIDATES synchronously and replies immediately; the
    /// multi-second adapter/route bring-up (or teardown) runs on a service
    /// worker thread, never while a pipe request is held open. The GUI learns
    /// the outcome the same way it always tracked progress: by polling STATUS
    /// (and pumping LOG). This is what lets the GUI run unelevated
    /// (asInvoker): SCM start/stop — which needs admin — is no longer the
    /// activation mechanism.
    ///
    /// Commands (client -> server), one line per connection:
    ///   PING                       -> "PONG"
    ///   STATUS                     -> "INACTIVE" or "ACTIVE|name|state|tx|rx|hsUnixSeconds|latencyMs|endpoint"
    ///   LOG                        -> "LOG|" + escaped recent service log
    ///   ACTIVATE|name              -> "OK" / "ERR|reason" / "DENIED"
    ///   ACTIVATE2|name|escapedConf -> "OK" / "ERR|reason" / "DENIED"   (portable: config text supplied by the GUI)
    ///   DEACTIVATE                 -> "OK" / "DENIED"
    ///   SAVECONFIG|name|escapedConf-> "OK" / "ERR|reason" / "DENIED"   (machine store write, done as SYSTEM)
    ///   DELETECONFIG|name          -> "OK" / "ERR|reason" / "DENIED"
    ///
    /// PING/STATUS/LOG remain open to any authenticated user (read-only).
    /// Everything else is a CONTROL command: the service verifies the client
    /// user is a member of Administrators — including via the UAC linked
    /// token, so the unelevated GUI of an admin passes with no prompt — and
    /// answers "DENIED" otherwise (see PipeClientAuth).
    ///
    /// Escaping (Escape/Unescape below): payloads that may contain newlines
    /// or backslashes (config text, the LOG snapshot) travel with '\\' and
    /// '\n' escaped so every message stays a single line. '|' needs no
    /// escaping: commands are split on their FIRST one or two separators
    /// only, and tunnel names can't contain '|' (ConfigStore.SanitizeName
    /// strips it).
    /// </summary>
    public static class ServiceProtocol
    {
        public const string PipeName = "WgSharp_Control";

        /// <summary>
        /// Escapes a multi-line payload into a single pipe line. Lossless:
        /// backslash, CR, and LF are encoded, everything else passes through,
        /// so config text round-trips byte-identically (a WinForms editor
        /// expects its CRLFs back exactly as saved).
        /// </summary>
        public static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        /// <summary>Reverses Escape.</summary>
        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static string FormatStatus(string name, TunnelStatus s)
        {
            long hs = s.LastHandshakeTime == DateTime.MinValue
                ? 0
                : (long)(s.LastHandshakeTime.ToUniversalTime() - Epoch).TotalSeconds;
            return "ACTIVE|" + name + "|" + s.State + "|" + s.TxBytes + "|" + s.RxBytes +
                   "|" + hs + "|" + s.LatencyMs + "|" + s.Endpoint;
        }

        /// <summary>Parses a STATUS response. Returns null (and name=null) for "INACTIVE" or anything malformed.</summary>
        public static TunnelStatus ParseStatus(string line, out string name)
        {
            name = null;
            if (string.IsNullOrEmpty(line) || line == "INACTIVE") return null;
            string[] p = line.Split('|');
            if (p.Length < 8 || p[0] != "ACTIVE") return null;

            name = p[1];
            var s = new TunnelStatus { State = p[2] };
            long tx, rx, hsSeconds, lat;
            long.TryParse(p[3], out tx); s.TxBytes = tx;
            long.TryParse(p[4], out rx); s.RxBytes = rx;
            long.TryParse(p[5], out hsSeconds);
            s.LastHandshakeTime = hsSeconds > 0 ? Epoch.AddSeconds(hsSeconds).ToLocalTime() : DateTime.MinValue;
            long.TryParse(p[6], out lat); s.LatencyMs = lat;
            s.Endpoint = p[7];
            return s;
        }
    }
}

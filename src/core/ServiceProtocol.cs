using System;

namespace WgSharp.Core
{
    /// <summary>
    /// The wire format for the named pipe between the GUI and the background
    /// service. Deliberately tiny and text-based: one line in, one line out.
    ///
    /// IMPORTANT: the pipe is used ONLY for quick runtime queries that return
    /// instantly. Activation and deactivation are NOT pipe commands — they are
    /// SCM start/stop of the service (see RemoteTunnelBackend / ServiceInstaller),
    /// modeled on the official WireGuard client, where the slow tunnel bring-up
    /// is the service's own SCM-managed startup, not a message held open on a
    /// pipe. An earlier version did send an ACTIVATE command and waited for the
    /// whole bring-up on that one request, which hung the pipe and broke things.
    ///
    /// Commands (client -> server), one per connection:
    ///   PING    -> "PONG"
    ///   STATUS  -> "INACTIVE" or "ACTIVE|name|state|tx|rx|hsUnixSeconds|latencyMs|endpoint"
    /// </summary>
    public static class ServiceProtocol
    {
        public const string PipeName = "WgSharp_Control";
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

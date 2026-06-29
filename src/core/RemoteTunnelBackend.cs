using System;

namespace WgSharp.Core
{
    /// <summary>
    /// An ITunnelBackend that doesn't run a tunnel itself — it drives the
    /// background service, which runs the tunnel as a LocalSystem process.
    ///
    /// Modeled on the official WireGuard for Windows client: activation is
    /// STARTING the service (SCM runs the bring-up inside the service's
    /// OnStart, and SCM owns that multi-second operation), and deactivation
    /// is STOPPING it. This replaces an earlier, broken approach that sent an
    /// "activate" COMMAND over a named pipe and waited for the whole bring-up
    /// to finish on that one request — which held the pipe open ~1s, timed
    /// out, and tore the tunnel back down. SCM is the right tool for a long-
    /// running start; the pipe is used only for the quick STATUS query.
    /// </summary>
    public sealed class RemoteTunnelBackend : ITunnelBackend
    {
        public event Action<string> LogMessage;
        /// <summary>
        /// Pre-stamped service log lines forwarded to the GUI (see
        /// PumpServiceLog). Distinct from LogMessage because these already
        /// carry the service's own timestamp and shouldn't be re-stamped by
        /// the GUI's normal Log path.
        /// </summary>
        public event Action<string> ServiceLogLine;
        private readonly string _name;

        public RemoteTunnelBackend(string tunnelName)
        {
            _name = tunnelName;
        }

        private void Log(string m) { var h = LogMessage; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Service")); }

        public void Start()
        {
            Log("Starting the background service for '" + _name + "'\u2026");
            // Tell the service which tunnel to bring up, THEN start it. The
            // service reads this in its OnStart. (Set-then-start, never a
            // command race: the name is durably persisted before SCM launches
            // the service, so the service always sees the right tunnel.)
            ServiceState.SetLastTunnel(_name);
            try
            {
                ServiceInstaller.StartTunnelService();
                // While connected through the service, flip it to auto-start so
                // a reboot reconnects this tunnel before login. Cleared back to
                // demand on Stop(), so an explicit disconnect stays disconnected
                // across reboots.
                ServiceInstaller.SetBootStart(true);
                Log("Background service started; tunnel '" + _name + "' is coming up.");
            }
            catch (Exception ex)
            {
                // Starting failed (or the service's own bring-up failed and it
                // stopped itself, which surfaces as a start timeout/failure).
                ServiceState.Clear();
                ServiceInstaller.SetBootStart(false);
                throw new Exception("The background service did not start the tunnel: " + ex.Message);
            }
        }

        public void Stop()
        {
            Log("Stopping the background service\u2026");
            // Explicit disconnect: don't reconnect this at the next boot.
            ServiceInstaller.SetBootStart(false);
            ServiceState.Clear();
            ServiceInstaller.StopTunnelService();
        }

        public TunnelStatus GetStatus()
        {
            // Status is the one thing the pipe is genuinely good for: a quick
            // runtime query that returns immediately. If the service isn't
            // reachable yet (still starting), report a Handshaking-ish state
            // rather than failing — the GUI's status poll will catch up.
            string err;
            string resp = ServiceClient.SendCommand("STATUS", out err);
            if (!_loggedFirstStatus)
            {
                _loggedFirstStatus = true;
                Log(WgSharp.Core.Logger.DebugMarker + "First STATUS response from service: " +
                    (resp == null ? "<null> (" + (err ?? "unknown") + ")" : "\"" + resp + "\""));
            }
            string name;
            TunnelStatus s = ServiceProtocol.ParseStatus(resp, out name);
            if (s != null) return s;

            // No status yet: reflect whether the service is at least running.
            var fallback = new TunnelStatus();
            fallback.State = ServiceInstaller.IsRunning() ? "Handshaking" : "Idle";
            return fallback;
        }

        private bool _loggedFirstStatus;

        // Tracks how much of the service's in-memory log we've already
        // forwarded to the GUI's Log tab, so PumpServiceLog only emits lines
        // that are genuinely new. The service stamps every line with a
        // millisecond timestamp, so exact-string dedup against the last line
        // we showed is reliable even when several lines share a second.
        private string _lastServiceLogLine;

        /// <summary>
        /// Pulls the service's recent in-memory log over the pipe and forwards
        /// any lines newer than the last one we've already shown into the GUI's
        /// Log tab (via LogMessage). Cheap STATUS-class pipe call; the GUI
        /// invokes it from its once-per-second status tick while a
        /// service-driven tunnel is active, so service activity shows up in the
        /// same Log tab as everything else — no service.log file needed.
        /// The lines already carry their own component tags and (when present)
        /// debug markers, so the GUI's normal filter applies to them too.
        /// </summary>
        public void PumpServiceLog()
        {
            string[] lines;
            try { lines = ServiceClient.FetchServiceLog(); }
            catch { return; }
            if (lines == null || lines.Length == 0) return;

            int startAt = 0;
            if (_lastServiceLogLine != null)
            {
                // Find the last line we already showed; emit everything after it.
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (lines[i] == _lastServiceLogLine) { startAt = i + 1; break; }
                }
            }

            var h = ServiceLogLine;
            for (int i = startAt; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                if (h != null) h(FormatForwarded(ln));
            }
            _lastServiceLogLine = lines[lines.Length - 1];
        }

        // Service ring lines look like "2026-... HH:mm:ss.fff <message>" — the
        // timestamp is already first. We insert a "[Service]" tag AFTER the
        // timestamp (not before it), so the GUI Log tab keeps date/time first
        // for every line, with the brackets after: "2026-... [Service] <msg>".
        private static string FormatForwarded(string ringLine)
        {
            // The service stamp is "yyyy-MM-dd HH:mm:ss.fff " = 23 chars + a
            // space before the message. Split on the first 24 chars if it looks
            // like a timestamp; otherwise just prefix the tag.
            const int stampLen = 23; // "2026-06-29 13:03:33.597"
            if (ringLine.Length > stampLen + 1 &&
                ringLine[4] == '-' && ringLine[7] == '-' && ringLine[13] == ':')
            {
                string stamp = ringLine.Substring(0, stampLen);
                string rest = ringLine.Substring(stampLen).TrimStart();
                return stamp + " [Service] " + rest;
            }
            return "[Service] " + ringLine;
        }
    }
}

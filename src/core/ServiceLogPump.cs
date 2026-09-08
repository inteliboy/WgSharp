using System;

namespace WgSharp.Core
{
    /// <summary>
    /// Forwards new lines from the background service's in-memory log ring
    /// (ServiceClient.FetchServiceLog, the LOG pipe command) into the GUI's
    /// Log tab.
    ///
    /// Deliberately a single shared pump, not owned by any particular tunnel
    /// object: the service logs plenty even when idle (its own startup, driver
    /// bootstrap, a boot-time reconnect that FAILED and so left nothing
    /// active), and RemoteTunnelBackend instances come and go with every
    /// activate/deactivate. An earlier version of this lived as an instance
    /// method on RemoteTunnelBackend and was only ever pumped while
    /// MainForm._tunnel was a service-driven tunnel — so a restart where the
    /// service started up (or tried and failed to reconnect) with nothing
    /// ending up active meant the GUI never asked for the log at all, and the
    /// user had no way to see what the service had actually done. Pumped by
    /// MainForm on startup and on a standing timer regardless of whether a
    /// tunnel is active, so that gap is closed.
    /// </summary>
    public static class ServiceLogPump
    {
        /// <summary>
        /// Pre-stamped service log lines, oldest first. Distinct from the
        /// GUI's normal Log path because these already carry the service's
        /// own timestamp and shouldn't be re-stamped.
        /// </summary>
        public static event Action<string> LogLine;

        // Guards both the pipe round trip and the dedup state so concurrent
        // callers (the standing UI timer and an eager post-activation pump,
        // say) can't interleave and either drop or duplicate lines.
        private static readonly object _gate = new object();
        private static string _lastLine;

        public static void Pump()
        {
            lock (_gate)
            {
                string[] lines;
                try { lines = ServiceClient.FetchServiceLog(); }
                catch { return; }
                if (lines == null || lines.Length == 0) return;

                // Find the last line we've already shown; emit everything
                // after it. The service stamps every line with a millisecond
                // timestamp, so exact-string dedup against the last line we
                // showed is reliable even when several lines share a second.
                int startAt = 0;
                if (_lastLine != null)
                {
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        if (lines[i] == _lastLine) { startAt = i + 1; break; }
                    }
                }

                var h = LogLine;
                for (int i = startAt; i < lines.Length; i++)
                {
                    string ln = lines[i];
                    if (ln.Length == 0) continue;
                    if (h != null) h(FormatForwarded(ln));
                }
                _lastLine = lines[lines.Length - 1];
            }
        }

        // Service ring lines look like "2026-... HH:mm:ss.fff <message>" — the
        // timestamp is already first. We insert a "[Service]" tag AFTER the
        // timestamp (not before it), so the GUI Log tab keeps date/time first
        // for every line, with the brackets after: "2026-... [Service] <msg>".
        private static string FormatForwarded(string ringLine)
        {
            // The service stamp is "yyyy-MM-dd HH:mm:ss.fff " = 23 chars + a
            // space before the message. Split on the first 24 chars if it
            // looks like a timestamp; otherwise just prefix the tag.
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

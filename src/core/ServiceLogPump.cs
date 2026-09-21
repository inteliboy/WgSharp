using System;

namespace WgSharp.Core
{
    /// <summary>
    /// Forwards new lines from the background service's in-memory log ring
    /// (ServiceClient.FetchServiceLogSince, the LOG2 pipe command) into the
    /// GUI's Log tab.
    ///
    /// Deliberately a single shared pump, not owned by any particular tunnel
    /// object: the service logs plenty even when idle (its own startup, driver
    /// bootstrap, a boot-time reconnect that FAILED and so left nothing
    /// active), and RemoteTunnelBackend instances come and go with every
    /// activate/deactivate. Pumped by MainForm on startup and on a standing
    /// timer regardless of whether a tunnel is active.
    ///
    /// Pump() does blocking pipe I/O and must NOT be called on the UI thread
    /// (MainForm runs it on the thread pool). It is incremental: the service
    /// hands back only lines added since the last call, so an idle poll is a
    /// few bytes rather than the whole ring. If the service is unreachable the
    /// pump backs off instead of retrying (and blocking on the connect
    /// timeout) every second.
    /// </summary>
    public static class ServiceLogPump
    {
        /// <summary>
        /// Pre-stamped service log lines, oldest first. Distinct from the
        /// GUI's normal Log path because these already carry the service's
        /// own timestamp and shouldn't be re-stamped. Raised on the calling
        /// (pool) thread; subscribers must marshal to the UI themselves.
        /// </summary>
        public static event Action<string> LogLine;

        // Guards both the pipe round trip and the cursor/dedup state so
        // concurrent callers can't interleave and either drop or duplicate
        // lines.
        private static readonly object _gate = new object();

        // LOG2 cursor.
        private static string _epoch;
        private static long _cursor;
        private static bool _legacy;        // service doesn't know LOG2: use full LOG + dedup
        private static string _lastLine;    // legacy path only

        private const int BackoffMs = 5000;
        private static int _retryAt;        // Environment.TickCount; 0 = no backoff pending
        private static bool _backingOff;

        public static void Pump()
        {
            lock (_gate)
            {
                if (_backingOff && unchecked(Environment.TickCount - _retryAt) < 0) return;
                _backingOff = false;

                string[] lines;
                bool reachable;
                if (!_legacy)
                {
                    bool supported;
                    reachable = ServiceClient.FetchServiceLogSince(ref _epoch, ref _cursor,
                                                                   out lines, out supported);
                    if (!reachable && !supported)
                    {
                        _legacy = true; // older service: fall through to LOG this time
                    }
                    else
                    {
                        if (!reachable) { BackOff(); return; }
                        Emit(lines, 0);
                        return;
                    }
                }

                lines = ServiceClient.FetchServiceLog();
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
                Emit(lines, startAt);
                _lastLine = lines[lines.Length - 1];
            }
        }

        private static void BackOff()
        {
            _backingOff = true;
            _retryAt = unchecked(Environment.TickCount + BackoffMs);
        }

        /// <summary>
        /// Call after something that should make the service reachable soon
        /// (an activation just went through) to drop any pending backoff.
        /// </summary>
        public static void ResetBackoff()
        {
            lock (_gate) { _backingOff = false; }
        }

        private static void Emit(string[] lines, int startAt)
        {
            var h = LogLine;
            if (h == null) return;
            for (int i = startAt; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                h(FormatForwarded(ln));
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

using System;

namespace WgSharp.Core
{
    /// <summary>
    /// Central place that decides how much detail the Log surfaces show.
    ///
    /// Every component already emits messages through an <c>Action&lt;string&gt;</c>
    /// (the GUI's MainForm.Log and the service's LogLine). Verbose, diagnostic
    /// lines are marked at their call site with the <see cref="DebugMarker"/>
    /// prefix ("[dbg] "). When the user's "Debug log" setting is OFF (the
    /// default), <see cref="ShouldShow"/> drops those lines and only the
    /// meaningful ones (state changes, activation/deactivation, errors) get
    /// through; when it's ON, everything is shown.
    ///
    /// <see cref="Strip"/> removes the marker before display, so the user never
    /// sees the raw "[dbg]" token — the component tag (e.g. "[WireGuardNT]")
    /// that follows it is preserved.
    /// </summary>
    public static class Logger
    {
        public const string DebugMarker = "[dbg] ";

        /// <summary>True if this raw message should appear given the current Debug setting.</summary>
        public static bool ShouldShow(string raw)
        {
            if (raw == null) return false;
            if (IsDebug(raw)) return AppSettings.DebugLog;
            return true;
        }

        /// <summary>True if the message is a debug/verbose line (carries the marker).</summary>
        public static bool IsDebug(string raw)
        {
            return raw != null && raw.StartsWith(DebugMarker, StringComparison.Ordinal);
        }

        /// <summary>Removes the leading debug marker (if any) for display.</summary>
        public static string Strip(string raw)
        {
            if (raw == null) return null;
            return raw.StartsWith(DebugMarker, StringComparison.Ordinal)
                ? raw.Substring(DebugMarker.Length)
                : raw;
        }

        /// <summary>
        /// Prefixes <paramref name="m"/> with a component tag like "[Tunnel] ",
        /// keeping any leading "[dbg] " marker first so the verbosity filter
        /// still recognizes it: "[dbg] Foo" with tag "Tunnel" becomes
        /// "[dbg] [Tunnel] Foo". A message that already carries a (component)
        /// tag of its own is left as-is, except the marker is still floated to
        /// the front.
        /// </summary>
        public static string Tag(string m, string component)
        {
            if (m == null) m = "";
            bool dbg = m.StartsWith(DebugMarker, StringComparison.Ordinal);
            string body = dbg ? m.Substring(DebugMarker.Length) : m;
            // Body already has its own bracketed tag? leave it; otherwise add ours.
            if (!(body.Length > 0 && body[0] == '['))
                body = "[" + component + "] " + body;
            return dbg ? DebugMarker + body : body;
        }
    }
}

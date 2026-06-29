using System;

namespace WgSharp.Tun
{
    /// <summary>
    /// "Block untunneled traffic" kill-switch, backed by the Windows Filtering
    /// Platform (see WfpKillSwitch). This is a thin wrapper that keeps a stable
    /// API for the tunnel backends and routes its logging to the UI.
    ///
    /// History: an earlier netsh-firewall-rule based implementation was removed.
    /// It used a block-all-outbound rule plus scoped allow rules, but Windows
    /// Defender Firewall always lets a block rule win over a competing allow rule
    /// regardless of how specific the allow rule's scope is - there's no
    /// "more specific allow overrides a block" mechanism for ordinary (non-IPsec)
    /// rules. That meant real tunnel traffic could never reliably pass once
    /// engaged; only the pre-engage handshake ever got through. WFP has no such
    /// categorical rule: filters in our sublayer are arbitrated purely by weight
    /// (permit=15 beats block=0), so the permit deterministically wins. Confirmed
    /// working on hardware: real traffic flows normally while engaged, and
    /// connectivity is restored immediately on disengage.
    /// </summary>
    public static class KillSwitch
    {
        public static event Action<string> Log;
        // WfpKillSwitch messages arrive already tagged "[WFP] ..."; only prefix
        // our own untagged messages so the log doesn't read "[KillSwitch] [WFP] ...".
        private static void L(string m)
        {
            var h = Log;
            if (h == null) return;
            h(WgSharp.Core.Logger.Tag(m, "KillSwitch"));
        }

        private static bool _engaged;
        private static bool _logHooked;

        public static void Engage(string endpointIp, string tunnelAddresses, ulong tunnelLuid)
        {
            if (_engaged) return;
            if (!_logHooked) { WfpKillSwitch.Log += L; _logHooked = true; }

            L("Kill-switch: engaging via WFP (endpoint=" +
              (string.IsNullOrEmpty(endpointIp) ? "none" : endpointIp) +
              ", tunnelIPs=" + (string.IsNullOrEmpty(tunnelAddresses) ? "none" : tunnelAddresses) +
              ", luid=" + tunnelLuid + ").");

            WfpKillSwitch.Engage(endpointIp, tunnelAddresses, tunnelLuid);
            _engaged = true;
            L("Kill-switch: ACTIVE via WFP.");
        }

        public static void Disengage()
        {
            if (!_engaged) return;
            L("Kill-switch: disengaging...");
            try { WfpKillSwitch.Disengage(); }
            catch (Exception ex) { L("Kill-switch teardown error: " + ex.Message); }
            _engaged = false;
            L("Kill-switch: disengaged.");
        }
    }
}

using System;

namespace WgSharp.Core
{
    /// <summary>
    /// An ITunnelBackend that doesn't run a tunnel itself — it drives the
    /// always-running background manager service, which runs the tunnel as a
    /// LocalSystem process. Activation and deactivation are pipe commands
    /// (ACTIVATE / ACTIVATE2 / DEACTIVATE) that the service validates and
    /// acknowledges INSTANTLY, running the multi-second bring-up/teardown on
    /// its own worker — so this class never holds the pipe open across slow
    /// work (the failure mode of a much earlier design), and it also never
    /// touches SCM or HKLM, which is what lets the GUI run unelevated with no
    /// UAC prompt. Progress and results are observed the same way as always:
    /// the STATUS poll and the pumped service LOG.
    /// </summary>
    public sealed class RemoteTunnelBackend : ITunnelBackend
    {
        public event Action<string> LogMessage;
        private readonly string _name;
        private readonly string _portableConfigText; // non-null => ACTIVATE2 (portable tunnel, decrypted by the GUI)

        public RemoteTunnelBackend(string tunnelName) : this(tunnelName, null) { }

        /// <summary>
        /// portableConfigText: for portable (password-encrypted) tunnels the
        /// GUI decrypts the config locally and passes the plaintext here; it
        /// travels to the service over the local, ACL'd pipe (ACTIVATE2) and
        /// is never persisted by the service. Null for normal machine-store
        /// tunnels, which the service loads itself by name.
        /// </summary>
        public RemoteTunnelBackend(string tunnelName, string portableConfigText)
        {
            _name = tunnelName;
            _portableConfigText = portableConfigText;
        }

        private void Log(string m) { var h = LogMessage; if (h != null) h(WgSharp.Core.Logger.Tag(m, "Service")); }

        public void Start()
        {
            Log("Asking the background service to activate '" + _name + "'\u2026");
            string cmd = _portableConfigText == null
                ? "ACTIVATE|" + _name
                : "ACTIVATE2|" + _name + "|" + ServiceProtocol.Escape(_portableConfigText);

            string err;
            string resp = ServiceClient.SendCommand(cmd, out err);

            if (resp == null)
                throw new Exception(
                    "The background service isn't reachable (" + (err ?? "no response") + "). " +
                    "If it was never set up, run the one-time administrator setup from the prompt at startup " +
                    "(or start WgSharp elevated once).");
            if (resp == "DENIED")
                throw new Exception(
                    "The background service refused the command: your Windows account must be a member of " +
                    "the Administrators group to control tunnels.");
            if (resp.StartsWith("ERR|", StringComparison.Ordinal))
                throw new Exception("The background service rejected the activation: " +
                                    ServiceProtocol.Unescape(resp.Substring(4)));
            if (resp != "OK")
                throw new Exception("Unexpected service reply: " + resp);

            // Accepted: the bring-up continues on the service's worker; the
            // GUI's STATUS poll and log pump take it from here (a failed
            // bring-up shows up as the service logging the error and STATUS
            // returning to INACTIVE).
            Log("Background service accepted the activation; tunnel '" + _name + "' is coming up.");
        }

        public void Stop()
        {
            Log("Asking the background service to deactivate\u2026");
            string err;
            string resp = ServiceClient.SendCommand("DEACTIVATE", out err);
            if (resp == null)
                Log("Deactivate: service not reachable (" + (err ?? "no response") + ") — if the service is " +
                    "stopped, the tunnel is already down.");
            else if (resp == "DENIED")
                throw new Exception("The background service refused the command: your Windows account must be " +
                                    "a member of the Administrators group to control tunnels.");
            else if (resp != "OK")
                Log("Deactivate: unexpected service reply: " + resp);
            // Teardown continues on the service's worker; STATUS flips to
            // INACTIVE when it's done.
        }

        public TunnelStatus GetStatus()
        {
            // Status is the one thing the pipe is genuinely good for: a quick
            // runtime query that returns immediately. If the service isn't
            // reachable yet (still starting), report a Handshaking-ish state
            // rather than failing — the GUI's status poll will catch up.
            //
            // On ARM64 (x64 emulation), service startup is slower and the
            // first few STATUS polls race against the pipe becoming ready.
            // Retry a handful of times with a short sleep before giving up
            // and logging the null — the GUI polls every ~2s anyway, so a
            // brief retry here is invisible to the user but avoids a confusing
            // "null" in the debug log on every normal ARM64 startup.
            string err = null;
            string resp = null;
            int maxTries = _loggedFirstStatus ? 1 : 4;
            for (int i = 0; i < maxTries; i++)
            {
                resp = ServiceClient.SendCommand("STATUS", out err);
                if (resp != null) break;
                if (i < maxTries - 1) System.Threading.Thread.Sleep(300);
            }

            if (!_loggedFirstStatus)
            {
                _loggedFirstStatus = true;
                Log(WgSharp.Core.Logger.DebugMarker + "First STATUS response from service: " +
                    (resp == null ? "<null> (" + (err ?? "unknown") + ")" : "\"" + resp + "\""));
            }
            string name;
            TunnelStatus s = ServiceProtocol.ParseStatus(resp, out name);
            if (s != null) return s;

            // No ACTIVE status. With the always-running manager service,
            // INACTIVE means the tunnel is down (activation still starting,
            // failed, or deactivated). An unreachable pipe right after an
            // accepted ACTIVATE is the brief startup race — show Handshaking
            // so the UI doesn't flicker to Idle mid-bring-up; otherwise Idle.
            var fallback = new TunnelStatus();
            fallback.State = (resp == null) ? "Handshaking" : "Idle";
            return fallback;
        }

        private bool _loggedFirstStatus;
    }
}

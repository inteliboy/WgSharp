using System;
using System.Net;
using System.Net.NetworkInformation;

namespace WgSharp.Core
{
    /// <summary>
    /// Turns the once-a-second TunnelStatus snapshots into a plain-language
    /// hint about why a tunnel that looks "up" may not be passing traffic.
    /// Pure state machine over (status, now): no I/O, UI thread only.
    ///
    /// A stale handshake on its own is NOT a fault (an idle tunnel without
    /// keepalive legitimately never rekeys), so the "connected but dead"
    /// check keys on traffic: bytes are being sent and nothing comes back.
    /// </summary>
    public sealed class ConnectionDiagnostics
    {
        private const int HandshakeWarnSeconds = 10;
        private const int NoReplySeconds = 20;

        private string _state;
        private DateTime _stateSince;
        private long _lastRx = -1, _lastTx = -1;
        private DateTime _lastRxChange, _lastTxChange;

        public void Reset()
        {
            _state = null;
            _lastRx = _lastTx = -1;
        }

        /// <summary>
        /// Feeds one snapshot and returns the hint, or null when nothing
        /// looks wrong. awg adds an AmneziaWG-specific note.
        /// </summary>
        public string Update(TunnelStatus s, DateTime now, bool awg)
        {
            if (_state != s.State || _lastRx < 0)
            {
                if (_state != s.State) _stateSince = now;
                _state = s.State;
            }
            if (_lastRx < 0) { _lastRx = s.RxBytes; _lastTx = s.TxBytes; _lastRxChange = _lastTxChange = now; }
            if (s.RxBytes != _lastRx) { _lastRx = s.RxBytes; _lastRxChange = now; }
            if (s.TxBytes != _lastTx) { _lastTx = s.TxBytes; _lastTxChange = now; }

            double inState = (now - _stateSince).TotalSeconds;

            if (s.State == "Failed")
                return "The tunnel failed to start. The Log tab has the reason.";

            if (s.State == "Handshaking" && s.LastHandshakeTime == DateTime.MinValue && inState >= HandshakeWarnSeconds)
            {
                string t = "No reply from the endpoint after " + (int)inState + "s. Check that the endpoint host and " +
                           "UDP port are reachable (not blocked by a firewall or network), that the peer public key " +
                           "matches the server, and that this PC's clock is correct.";
                if (awg) t += " AmneziaWG parameters must match the server exactly.";
                return t;
            }

            if (s.State == "Connected" &&
                (now - _lastTxChange).TotalSeconds < 5 &&
                (now - _lastRxChange).TotalSeconds >= NoReplySeconds)
            {
                return "Sending traffic but receiving none for " + (int)(now - _lastRxChange).TotalSeconds +
                       "s. Check that the server lists this client's address in the peer's AllowedIPs and that " +
                       "its firewall/NAT forwards the traffic.";
            }
            return null;
        }

        /// <summary>
        /// Picks a host worth pinging through the tunnel: the first literal IP
        /// in the config's DNS list (it is routed through the tunnel even in
        /// split mode, see the DNS auto-fix), or null.
        /// </summary>
        public static IPAddress PingTarget(Config cfg)
        {
            if (cfg == null || string.IsNullOrEmpty(cfg.Dns)) return null;
            foreach (string part in cfg.Dns.Split(','))
            {
                IPAddress ip;
                if (IPAddress.TryParse(part.Trim(), out ip)) return ip;
            }
            return null;
        }

        /// <summary>Blocking: sends 4 echo requests and returns a one-line summary. Call from a worker thread.</summary>
        public static string Ping(IPAddress target)
        {
            int ok = 0, total = 4;
            long sum = 0, min = long.MaxValue, max = 0;
            using (var p = new Ping())
            {
                for (int i = 0; i < total; i++)
                {
                    try
                    {
                        PingReply r = p.Send(target, 2000);
                        if (r.Status == IPStatus.Success)
                        {
                            ok++;
                            sum += r.RoundtripTime;
                            if (r.RoundtripTime < min) min = r.RoundtripTime;
                            if (r.RoundtripTime > max) max = r.RoundtripTime;
                        }
                    }
                    catch (Exception) { }
                    if (i < total - 1) System.Threading.Thread.Sleep(300);
                }
            }
            if (ok == 0) return "Ping " + target + ": no reply (4/4 lost). The tunnel is up but not passing traffic to this host.";
            return "Ping " + target + ": " + ok + "/" + total + " replies, " + min + "/" + (sum / ok) + "/" + max +
                   " ms (min/avg/max)";
        }
    }
}

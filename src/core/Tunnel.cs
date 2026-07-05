using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using WgSharp.Crypto;
using WgSharp.Net;
using WgSharp.Proto;
using WgSharp.Tun;

namespace WgSharp.Core
{
    public sealed class TunnelStatus
    {
        public string State = "Idle";
        public string LastHandshake = "never";
        public DateTime LastHandshakeTime = DateTime.MinValue; // for elapsed display
        public long RxBytes;
        public long TxBytes;
        public string Endpoint = "";
        public long LatencyMs = -1;        // -1 => unknown / not measured
    }

    /// <summary>
    /// The engine. Owns the Wintun adapter, a shared UDP socket, and one PeerState
    /// per [Peer] section. Three worker threads (outbound, inbound, timers) pump
    /// data: outbound packets are routed to a peer by longest-prefix AllowedIPs
    /// match; inbound packets are demuxed to a peer by the message's receiver
    /// index. Initiator role only (dials each peer that has an Endpoint).
    /// </summary>
    public sealed class Tunnel : ITunnelBackend
    {
        public event Action<string> LogMessage;

        private readonly Config _cfg;
        private WintunAdapter _adapter;
        private UdpTransport _udp;
        private bool _killSwitchEngaged;
        private ulong _tunnelLuid;

        private readonly List<PeerState> _peers = new List<PeerState>();
        private AllowedIpRouter _router;

        private Thread _outbound, _inbound, _timers;
        private volatile bool _running;

        private readonly TunnelStatus _status = new TunnelStatus();
        private readonly object _statusLock = new object();
        private readonly object _hsLock = new object();

        public Tunnel(Config cfg) { _cfg = cfg; _awg = cfg.IsAmneziaWg; }

        // Cached once: true if this tunnel's config carries any AmneziaWG
        // extension keys. See AwgFraming.cs for what this actually changes —
        // everywhere _awg gates extra behavior in this file, the surrounding
        // standard-WireGuard code path is completely untouched when it's
        // false, so a normal tunnel's behavior is identical to before AWG
        // support existed.
        private readonly bool _awg;

        private void Log(string m)
        {
            var h = LogMessage;
            if (h == null) return;
            // AdapterConfig messages arrive already tagged "[AdapterConfig] ...";
            // only prefix our own untagged messages.
            h(WgSharp.Core.Logger.Tag(m, "Tunnel"));
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            lock (_statusLock) { _status.State = "Starting"; _status.Endpoint = _cfg.Endpoint; }

            // Make sure the Wintun driver DLL is available next to the exe.
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!WintunDownloader.IsPresent(baseDir))
            {
                Log("wintun.dll not found; fetching it from wintun.net\u2026");
                WintunDownloader.Log += Log;
                try { WintunDownloader.EnsurePresent(baseDir); }
                catch (Exception ex)
                {
                    throw new Exception("Could not obtain wintun.dll automatically: " + ex.Message +
                        "\nYou can place it next to the executable manually from https://www.wintun.net.");
                }
            }

            _adapter = new WintunAdapter("WgSharp", "WgSharp");
            ulong luid = _adapter.GetLuid();
            _tunnelLuid = luid;
            Log(WgSharp.Core.Logger.DebugMarker + "Wintun adapter up (LUID=" + luid + ")");

            // Build the peer list and the AllowedIPs router.
            BuildPeers();
            if (_peers.Count == 0) throw new Exception("No peers in configuration.");
            if (_peers.Count > 1) Log(_peers.Count + " peers configured; routing by AllowedIPs.");

            if (_awg)
                Log("AmneziaWG mode active (Jc=" + _cfg.EffectiveJc + " Jmin=" + _cfg.EffectiveJmin +
                    " Jmax=" + _cfg.EffectiveJmax + " S1=" + _cfg.EffectiveS1 + " S2=" + _cfg.EffectiveS2 +
                    "); disguising the connection. This config can only run on the managed " +
                    "backend, never WireGuardNT.");

            // Assign the interface address.
            AdapterConfig.Log += Log;
            IPAddress ifAddr; int ifPrefix;
            if (AdapterConfig.TryParseCidr(_cfg.Address, out ifAddr, out ifPrefix))
                AdapterConfig.SetAddress(luid, ifAddr, ifPrefix);
            else
                Log("No usable interface Address in config; tunnel will not route.");

            // DNS + MTU.
            try { AdapterConfig.SetDns(luid, _cfg.DnsServers(), _cfg.DnsSearchDomains()); }
            catch (Exception ex) { Log("DNS setup skipped: " + ex.Message); }
            try { AdapterConfig.SetMtu(luid, _cfg.Mtu); }
            catch (Exception ex) { Log("MTU setup skipped: " + ex.Message); }

            // Cosmetic: nudge Windows to present the Wintun adapter as connected
            // so the Network Connections UI shows the assigned IP/DNS (WireGuardNT
            // gets this for free from its driver's SetAdapterState(UP); Wintun has
            // no such call). Best-effort — doesn't affect whether traffic flows.
            try { AdapterConfig.SetInterfaceConnected(luid); }
            catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Interface-connected nudge skipped: " + ex.Message); }

            // One shared UDP socket. Its default _peer is the first peer's endpoint
            // (kept for compatibility); per-peer sends use SendTo with each
            // peer's resolved endpoint.
            string firstEndpoint = _peers[0].EndpointSpec;
            _udp = new UdpTransport(firstEndpoint, _cfg.ListenPort);
            Log(WgSharp.Core.Logger.DebugMarker + "UDP bound; first peer " + _udp.PeerEndpoint);

            // Resolve every peer's endpoint and pin a /32 host route to it when this
            // tunnel routes all traffic (so encrypted packets to each server bypass
            // the tunnel and the handshake isn't blackholed).
            bool routesAll = RoutesAllTraffic();
            IPAddress gw = routesAll ? AdapterConfig.FindDefaultGateway("WgSharp") : null;
            foreach (PeerState p in _peers)
            {
                if (!p.HasEndpoint) continue;
                try
                {
                    p.Endpoint = UdpTransport.ResolveEndpoint(p.EndpointSpec);
                    p.EndpointAddress = p.Endpoint.Address;
                }
                catch (Exception ex) { Log("Could not resolve endpoint for a peer: " + ex.Message); continue; }

                if (routesAll && gw != null && p.EndpointAddress != null)
                {
                    if (AdapterConfig.PinEndpointRoute(p.EndpointAddress, gw))
                        p.EndpointPinned = true;
                }
            }
            if (routesAll && gw == null)
                Log("WARNING: no default gateway found to pin endpoint routes; full-tunnel handshake may stall.");

            // Install the union of all peers' AllowedIPs as interface routes.
            foreach (PeerState p in _peers)
            {
                Config.Peer cp = _cfg.Peers[p.Index];
                foreach (string allowed in cp.AllowedIPs)
                {
                    IPAddress dest; int prefix;
                    if (!AdapterConfig.TryParseCidr(allowed, out dest, out prefix)) continue;
                    AdapterConfig.AddRouteForAllowedIp(luid, dest, prefix);
                }
            }

            // For split-tunnel configs (AllowedIPs doesn't cover everything), any
            // DNS server configured on the tunnel interface MUST be reachable via
            // the tunnel — because the OS will send DNS queries out through it. If
            // the DNS server's IP isn't covered by AllowedIPs, those queries go into
            // the tunnel but there's no route back, so the handshake stalls waiting
            // for a response that can never arrive. Fix: add a /32 host route through
            // the tunnel for every DNS server not already covered by AllowedIPs. This
            // is exactly what the official WireGuard client does under "wg-quick".
            // Skipped for full-tunnel (everything is already covered) and skipped
            // for servers already covered by an existing AllowedIPs entry (no point
            // adding a redundant more-specific route in that case).
            // Install the hidden DNS routes (computed in BuildPeers) as tunnel
            // interface routes — the OS-routing half of treating them as
            // AllowedIPs. Combined with the _router.Add done in BuildPeers, a
            // DNS server not listed in AllowedIPs is now handled EXACTLY as if
            // the user had added it manually (which they confirmed works): the
            // OS routes its packets to the tunnel adapter, AND the outbound
            // loop's AllowedIPs lookup now matches it so those packets are
            // actually encrypted and sent rather than dropped. Not shown in the
            // visible AllowedIPs — it's an implied route, like wg-quick's DNS
            // handling.
            foreach (string dnsCidr in _hiddenDnsRoutes)
            {
                IPAddress dnsDest; int dnsPrefix;
                if (!AdapterConfig.TryParseCidr(dnsCidr, out dnsDest, out dnsPrefix)) continue;
                Log("DNS server " + dnsCidr + " isn't in AllowedIPs; routing it through the tunnel " +
                    "automatically so DNS resolves (implied route, not shown in AllowedIPs).");
                AdapterConfig.AddRoute(luid, dnsDest, dnsPrefix);
            }

            // Force a low, fixed interface metric. A per-route metric=0 alone isn't
            // always enough — Windows can compute the EFFECTIVE metric as interface
            // metric + route metric, and a virtual adapter's automatic interface
            // metric can still lose to the physical NIC. This removes that ambiguity.
            AdapterConfig.SetInterfaceMetric(luid, 1);

            // Log the active default-route table so we can see, not guess, which
            // route Windows actually selected.
            AdapterConfig.DumpRouteDiagnostics(luid);

            // Kill-switch is deferred until the first handshake completes (Windows
            // Firewall block-precedence would otherwise drop the handshake).
            // (KillSwitch.Log is hooked once in MainForm, not per-connect.)
            Log("Kill-switch requested by config: " + (_cfg.BlockUntunneled ? "YES" : "no") +
                (_cfg.BlockUntunneled ? " (will engage after first handshake)." : "."));

            _outbound = new Thread(OutboundLoop) { IsBackground = true, Name = "wg-outbound" };
            _inbound = new Thread(InboundLoop) { IsBackground = true, Name = "wg-inbound" };
            _timers = new Thread(TimerLoop) { IsBackground = true, Name = "wg-timers" };
            _outbound.Start();
            _inbound.Start();
            _timers.Start();

            // Kick off a handshake to each peer that has an endpoint.
            foreach (PeerState p in _peers)
                if (p.HasEndpoint) InitiateHandshake(p);
            lock (_statusLock) _status.State = "Handshaking";
        }

        private void BuildPeers()
        {
            _peers.Clear();
            _router = new AllowedIpRouter();
            for (int i = 0; i < _cfg.Peers.Count; i++)
            {
                Config.Peer cp = _cfg.Peers[i];
                if (cp.PublicKey == null) continue;
                var ps = new PeerState(i, cp);
                _peers.Add(ps);
                foreach (string cidr in cp.AllowedIPs)
                    _router.Add(cidr, _peers.Count - 1); // index within _peers
            }

            // Hidden DNS routing (see AddHiddenDnsRoutes / _hiddenDnsRoutes):
            // ensure any DNS server not already covered by AllowedIPs is still
            // TUNNELED. This is the routing half of "add the DNS server to
            // AllowedIPs" — without adding it to the router, the outbound loop's
            // Lookup returns -1 for DNS packets and drops them, so DNS queries
            // never leave even if an OS route exists. We attribute these to the
            // first peer (the one carrying the tunnel), matching how a single-
            // peer config's DNS would be handled if it were listed in AllowedIPs.
            _hiddenDnsRoutes = ComputeHiddenDnsRoutes();
            if (_peers.Count > 0)
                foreach (string dnsCidr in _hiddenDnsRoutes)
                    _router.Add(dnsCidr, 0);
        }

        // DNS-server /32s that we route through the tunnel WITHOUT listing them
        // in the visible AllowedIPs — computed once in BuildPeers, reused when
        // installing OS routes. Only populated for split-tunnel configs where a
        // DNS server isn't already covered by a real AllowedIPs entry.
        private System.Collections.Generic.List<string> _hiddenDnsRoutes =
            new System.Collections.Generic.List<string>();

        private System.Collections.Generic.List<string> ComputeHiddenDnsRoutes()
        {
            var result = new System.Collections.Generic.List<string>();
            if (RoutesAllTraffic()) return result; // full-tunnel already covers everything

            var dnsServers = _cfg.DnsServers();
            if (dnsServers.Count == 0) return result;

            // Flatten all AllowedIPs (v4) for coverage checks.
            var allowedCidrs = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<uint, int>>();
            foreach (Config.Peer cp in _cfg.Peers)
                foreach (string allowed in cp.AllowedIPs)
                {
                    IPAddress dest; int prefix;
                    if (!AdapterConfig.TryParseCidr(allowed, out dest, out prefix)) continue;
                    if (dest.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    byte[] db = dest.GetAddressBytes();
                    uint net = (uint)(db[0] << 24 | db[1] << 16 | db[2] << 8 | db[3]);
                    allowedCidrs.Add(new System.Collections.Generic.KeyValuePair<uint, int>(net, prefix));
                }

            foreach (string srv in dnsServers)
            {
                IPAddress srvIp;
                if (!IPAddress.TryParse(srv, out srvIp)) continue;
                if (srvIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue; // v4 only
                byte[] sb = srvIp.GetAddressBytes();
                uint srvUint = (uint)(sb[0] << 24 | sb[1] << 16 | sb[2] << 8 | sb[3]);
                bool covered = false;
                foreach (var cidr in allowedCidrs)
                {
                    if (cidr.Value == 0) { covered = true; break; }
                    uint mask = cidr.Value == 32 ? 0xFFFFFFFFu : ~(0xFFFFFFFFu >> cidr.Value);
                    if ((srvUint & mask) == (cidr.Key & mask)) { covered = true; break; }
                }
                if (!covered) result.Add(srv + "/32");
            }
            return result;
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;

            if (_outbound != null) _outbound.Join(1500);
            if (_inbound != null) _inbound.Join(1500);
            if (_timers != null) _timers.Join(1500);

            if (_cfg.BlockUntunneled)
            {
                try { KillSwitch.Disengage(); }
                catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Kill-switch teardown error: " + ex.Message); }
                _killSwitchEngaged = false;
            }

            foreach (PeerState p in _peers)
            {
                if (p.EndpointPinned && p.EndpointAddress != null)
                {
                    try { AdapterConfig.UnpinEndpointRoute(p.EndpointAddress); }
                    catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Endpoint route cleanup error: " + ex.Message); }
                    p.EndpointPinned = false;
                }
                // Dispose CNG key handles held by the session (if any).
                if (p.Session != null) { try { p.Session.Dispose(); } catch { } p.Session = null; }
                if (p.Pending != null) { p.Pending = null; }
            }

            if (_udp != null) { _udp.Dispose(); _udp = null; }
            if (_adapter != null) { _adapter.Dispose(); _adapter = null; }
            _peers.Clear();

            // Wipe private key material from the Config object now that the
            // tunnel is fully stopped and no thread can be using it any more.
            // The Config object itself stays alive (held by whoever called
            // Tunnel(cfg)), but its sensitive fields are zeroed so a memory
            // dump taken after disconnect won't expose them.
            if (_cfg.PrivateKey != null) Array.Clear(_cfg.PrivateKey, 0, _cfg.PrivateKey.Length);
            if (_cfg.PresharedKey != null) Array.Clear(_cfg.PresharedKey, 0, _cfg.PresharedKey.Length);
            foreach (Config.Peer p in _cfg.Peers)
                if (p.PresharedKey != null) Array.Clear(p.PresharedKey, 0, p.PresharedKey.Length);

            lock (_statusLock) _status.State = "Idle";
            Log("Tunnel stopped.");
        }

        public TunnelStatus GetStatus()
        {
            lock (_statusLock)
            {
                return new TunnelStatus
                {
                    State = _status.State,
                    LastHandshake = _status.LastHandshake,
                    LastHandshakeTime = _status.LastHandshakeTime,
                    RxBytes = _status.RxBytes,
                    TxBytes = _status.TxBytes,
                    Endpoint = _status.Endpoint,
                    LatencyMs = _status.LatencyMs
                };
            }
        }

        // True when AllowedIPs (across all peers) routes all IPv4 traffic.
        private bool RoutesAllTraffic()
        {
            bool low = false, high = false;
            foreach (Config.Peer p in _cfg.Peers)
                foreach (string ip in p.AllowedIPs)
                {
                    string v = ip.Replace(" ", "");
                    if (v == "0.0.0.0/0") return true;
                    if (v == "0.0.0.0/1") low = true;
                    if (v == "128.0.0.0/1") high = true;
                }
            return low && high;
        }

        // ---------------- handshake ----------------

        private void TryReResolveEndpoint(PeerState p)
        {
            try
            {
                if (!p.HasEndpoint) return;
                IPEndPoint fresh = UdpTransport.ResolveEndpoint(p.EndpointSpec);
                if (p.Endpoint == null || !fresh.Address.Equals(p.Endpoint.Address) || fresh.Port != p.Endpoint.Port)
                {
                    IPAddress old = p.EndpointAddress;
                    p.Endpoint = fresh;
                    p.EndpointAddress = fresh.Address;
                    Log(WgSharp.Core.Logger.DebugMarker + "Endpoint re-resolved to " + fresh.Address + ".");
                    if (p.EndpointPinned && old != null && !fresh.Address.Equals(old))
                    {
                        try { AdapterConfig.UnpinEndpointRoute(old); } catch { }
                        IPAddress gw = AdapterConfig.FindDefaultGateway("WgSharp");
                        if (gw != null && AdapterConfig.PinEndpointRoute(fresh.Address, gw))
                            p.EndpointPinned = true;
                    }
                }
            }
            catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Endpoint re-resolve error: " + ex.Message); }
        }

        private void InitiateHandshake(PeerState p)
        {
            lock (_hsLock)
            {
                if (!p.HasEndpoint || p.Endpoint == null) return;
                var hs = new Handshake(_cfg.PrivateKey, p.PublicKey, p.PresharedKey);
                if (p.SavedCookie != null) hs.SeedCookie(p.SavedCookie, p.SavedCookieAt);
                byte[] init = hs.CreateInitiation(); // always standard WireGuard bytes; see Handshake.cs
                p.Pending = hs;
                p.LastHandshakeSent = DateTime.UtcNow;
                p.HandshakeAttempts++;

                // AWG: junk packets first, then the disguised initiation. Both
                // are sent on EVERY attempt (including retries), not just the
                // first — a retry is exactly as visible to DPI as the
                // original attempt, so it gets the same treatment.
                if (_awg)
                {
                    try
                    {
                        byte[][] junk = AwgFraming.BuildJunkPackets(_cfg.EffectiveJc, _cfg.EffectiveJmin, _cfg.EffectiveJmax);
                        foreach (byte[] j in junk)
                            _udp.SendTo(j, j.Length, p.Endpoint);
                    }
                    catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "AWG junk packet send failed: " + ex.Message); }

                    init = AwgFraming.WrapInitiation(init, _cfg.EffectiveH1, _cfg.EffectiveS1);
                }

                try { _udp.SendTo(init, init.Length, p.Endpoint); }
                catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Handshake send failed: " + ex.Message); }
                Log(WgSharp.Core.Logger.DebugMarker + "Handshake initiation sent to peer " + p.Index + " (attempt " + p.HandshakeAttempts + ").");
            }
        }

        // Find the peer whose pending handshake or session owns this local index.
        private PeerState FindByLocalIndex(uint localIndex, bool pending)
        {
            foreach (PeerState p in _peers)
            {
                if (pending)
                {
                    if (p.Pending != null && p.Pending.LocalIndex == localIndex) return p;
                }
                else
                {
                    Session s = p.Session;
                    if (s != null && s.LocalIndex == localIndex) return p;
                }
            }
            return null;
        }

        private void OnResponse(byte[] msg)
        {
            uint receiver = Messages.ReadLE32(msg, Messages.Resp_Receiver);
            lock (_hsLock)
            {
                PeerState p = FindByLocalIndex(receiver, true);
                if (p == null || p.Pending == null) return;
                TransportKeys keys = p.Pending.ConsumeResponse(msg);
                if (keys == null) { Log("Handshake response rejected (bad MAC/keys)."); return; }

                p.Session = new Session(keys);
                p.SessionEstablished = DateTime.UtcNow;
                p.LastRecv = DateTime.UtcNow;
                p.LastHandshakeOk = DateTime.UtcNow;
                p.HandshakeAttempts = 0;
                p.GaveUp = false;
                long rtt = (long)(DateTime.UtcNow - p.LastHandshakeSent).TotalMilliseconds;
                if (rtt >= 0 && rtt < 60000) p.LatencyMs = rtt;
                p.Pending = null;
                Log(WgSharp.Core.Logger.DebugMarker + "Crypto backend: " +
                    (p.Session.UsingCng ? "CNG (hardware-accelerated ChaCha20-Poly1305)" : "managed (pure .NET fallback)") + ".");

                lock (_statusLock)
                {
                    _status.State = "Connected";
                    _status.LastHandshakeTime = DateTime.Now;
                    _status.LastHandshake = DateTime.Now.ToString("HH:mm:ss");
                    if (p.LatencyMs >= 0) _status.LatencyMs = p.LatencyMs;
                }
                Log("Handshake complete with peer " + p.Index + " (local=" + keys.LocalIndex +
                    " remote=" + keys.RemoteIndex + ").");

                // Send an immediate keepalive (empty transport packet) to the
                // server as soon as the session is established. This is the
                // missing piece for split-tunnel configs: the handshake
                // initiation/response exchange proves the cryptographic identity
                // of both sides, but many servers don't mark the peer as
                // "active" for routing purposes until they see a real transport
                // packet from us — even an empty one. Without this, the tunnel
                // shows "Negotiating" indefinitely on a split-tunnel config
                // because no traffic flows naturally to trigger the first send
                // (unlike full-tunnel, where the OS floods traffic immediately).
                // Sending this here rather than from TimerLoop means we never
                // have to wait up to the PersistentKeepalive interval for the
                // server to acknowledge us. wg-quick achieves the same effect
                // via a PostUp ping; this is the equivalent, done in-process.
                SendKeepalive(p);

                if (_cfg.BlockUntunneled && !_killSwitchEngaged)
                {
                    try
                    {
                        string ep = p.EndpointAddress != null ? p.EndpointAddress.ToString() : null;
                        KillSwitch.Engage(ep, _cfg.Address, _tunnelLuid);
                        _killSwitchEngaged = true;
                    }
                    catch (Exception ex) { Log("Kill-switch could not be engaged: " + ex.Message); }
                }

                SendKeepalive(p);
            }
        }

        private void OnCookieReply(byte[] msg)
        {
            uint receiver = Messages.ReadLE32(msg, Messages.Cookie_Receiver);
            PeerState target = null;
            lock (_hsLock)
            {
                PeerState p = FindByLocalIndex(receiver, true);
                if (p != null && p.Pending != null && p.Pending.ConsumeCookieReply(msg))
                {
                    p.SavedCookie = p.Pending.CurrentCookie;
                    p.SavedCookieAt = p.Pending.CookieReceivedAt;
                    p.Pending = null;
                    p.HandshakeAttempts = 0;   // cookie accepted: fresh retry budget
                    p.GaveUp = false;
                    target = p;
                    Log(WgSharp.Core.Logger.DebugMarker + "Cookie reply accepted for peer " + p.Index + "; retrying with mac2.");
                }
                else
                {
                    Log(WgSharp.Core.Logger.DebugMarker + "Cookie reply received but could not be validated.");
                }
            }
            if (target != null && target.Session == null)
                InitiateHandshake(target);
        }

        // ---------------- outbound: OS -> peer ----------------
        private void OutboundLoop()
        {
            // Reusable packet buffer: WintunAdapter.ReceivePacket(byte[]) copies
            // each packet out of the ring into this and Session.Encrypt reads it
            // by (buffer, 0, length), encrypting straight into the outgoing
            // transport message — so nothing on this path allocates per packet
            // except the final UDP message itself.
            byte[] rxBuf = new byte[65536];
            while (_running)
            {
                if (!_adapter.WaitForPacket(250)) continue;
                int pktLen;
                long txThisDrain = 0;
                while ((pktLen = _adapter.ReceivePacket(rxBuf)) > 0)
                {
                    IPAddress dest = AllowedIpRouter.DestinationOf(rxBuf, pktLen);
                    PeerState p = null;
                    if (dest != null)
                    {
                        int idx = _router.Lookup(dest);
                        if (idx >= 0 && idx < _peers.Count) p = _peers[idx];
                    }
                    if (p == null && _peers.Count == 1) p = _peers[0];
                    if (p == null) continue;

                    Session s = p.Session;
                    if (s == null || p.Endpoint == null) continue;
                    try
                    {
                        byte[] msg = s.Encrypt(rxBuf, 0, pktLen);
                        if (_awg) msg = AwgFraming.WrapTransport(msg, _cfg.EffectiveH4);
                        _udp.SendTo(msg, msg.Length, p.Endpoint);
                        p.LastSent = DateTime.UtcNow;
                        txThisDrain += pktLen;
                    }
                    catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Outbound error: " + ex.Message); }
                }
                if (txThisDrain > 0) lock (_statusLock) { _status.TxBytes += txThisDrain; }
            }
        }

        // ---------------- inbound: peer -> OS ----------------
        private void InboundLoop()
        {
            // Reusable buffers: the socket fills recvBuf directly (no per-
            // datagram allocation), transport payloads decrypt into ptBuf, and
            // the adapter copies out of ptBuf into the Wintun ring before the
            // next iteration — so the steady-state data path allocates nothing.
            // Handshake-sized messages (rare: one every ~2 minutes) are copied
            // into exact-size arrays because their consumers validate Length.
            byte[] recvBuf = new byte[65536];
            byte[] ptBuf = new byte[65536];
            while (_running)
            {
                IPEndPoint from;
                int len;
                try { len = _udp.ReceiveFrom(recvBuf, out from); }
                catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Inbound recv error: " + ex.Message); continue; }
                if (len <= 0) continue;

                byte[] datagram = recvBuf;
                int dlen = len;
                if (_awg)
                {
                    // Strip the AWG disguise (S-byte padding, H-value header)
                    // back to a standard WireGuard message before anything
                    // below this point looks at it — OnResponse, OnCookieReply,
                    // and Session.Decrypt all expect standard bytes and are
                    // completely unaware AWG exists. Anything that doesn't
                    // match the expected shape under our own H/S values is
                    // noise (e.g. a stray reflected junk packet) and is
                    // silently dropped, same as a malformed packet always was.
                    // Transport messages are translated IN PLACE inside recvBuf
                    // (header rewrite only); Response/CookieReply come back as
                    // fresh exact-size arrays.
                    byte[] translated;
                    AwgFraming.InboundKind kind = AwgFraming.TranslateInbound(
                        recvBuf, len,
                        _cfg.EffectiveH2, _cfg.EffectiveH3, _cfg.EffectiveH4, _cfg.EffectiveS2,
                        out translated);
                    if (kind == AwgFraming.InboundKind.Unknown) continue;
                    datagram = translated;
                    dlen = ReferenceEquals(translated, recvBuf) ? len : translated.Length;
                }

                byte type = datagram[0];
                if (type == Messages.TypeResponse)
                {
                    if (dlen != Messages.ResponseSize) continue;
                    OnResponse(ExactCopy(datagram, dlen));
                }
                else if (type == Messages.TypeTransport)
                {
                    if (dlen < Messages.TransportHeaderSize) continue;
                    uint receiver = Messages.ReadLE32(datagram, Messages.Tr_Receiver);
                    PeerState p = FindByLocalIndex(receiver, false);
                    if (p == null) continue;
                    Session s = p.Session;
                    if (s == null) continue;
                    int ptLen;
                    if (!s.DecryptInto(datagram, dlen, ptBuf, out ptLen)) continue; // bad tag or replay
                    p.LastRecv = DateTime.UtcNow;
                    // Basic roaming: learn the peer's source address if it moved.
                    if (from != null && p.Endpoint != null && !from.Equals(p.Endpoint))
                    {
                        p.Endpoint = from;
                        p.EndpointAddress = from.Address;
                    }
                    if (ptLen == 0) continue;          // keepalive
                    try
                    {
                        _adapter.SendPacket(ptBuf, 0, ptLen);
                        // Update RxBytes under the existing status lock — inbound
                        // arrives one datagram at a time so batching doesn't help
                        // here the way it does on the outbound drain loop.
                        lock (_statusLock) { _status.RxBytes += ptLen; }
                    }
                    catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Inject error: " + ex.Message); }
                }
                else if (type == Messages.TypeCookieReply)
                {
                    if (dlen != Messages.CookieReplySize) continue;
                    OnCookieReply(ExactCopy(datagram, dlen));
                }
            }
        }

        // Copy the first n bytes into an exact-size array unless the source is
        // already exact. Handshake consumers (OnResponse / ConsumeCookieReply)
        // validate msg.Length, so messages arriving in the oversized reusable
        // receive buffer must be trimmed; AWG-translated ones already are.
        private static byte[] ExactCopy(byte[] src, int n)
        {
            if (src.Length == n) return src;
            byte[] r = new byte[n];
            Array.Copy(src, r, n);
            return r;
        }

        // ---------------- timers ----------------
        private void TimerLoop()
        {
            while (_running)
            {
                Thread.Sleep(250);
                DateTime now = DateTime.UtcNow;
                bool anyConnected = false;

                foreach (PeerState p in _peers)
                {
                    if (!p.HasEndpoint) continue;

                    // Handshake (re)transmission and give-up handling. We retry on
                    // RekeyTimeout intervals until the cumulative time since the
                    // first attempt exceeds the attempt budget, then give up until
                    // something resets it (a successful handshake or a cookie retry).
                    bool needInitiate = false;
                    lock (_hsLock)
                    {
                        if (p.Session == null && !p.GaveUp)
                        {
                            double since = (now - p.LastHandshakeSent).TotalSeconds;
                            bool dueToRetry = p.Pending != null &&
                                since >= Timers.RekeyTimeout + Timers.HandshakeJitterMs() / 1000.0;
                            bool neverStarted = p.Pending == null && p.HandshakeAttempts == 0;

                            if (dueToRetry || neverStarted)
                            {
                                double total = p.HandshakeAttempts * Timers.RekeyTimeout;
                                if (total > Timers.RekeyAttemptTime)
                                {
                                    Log("Handshake to peer " + p.Index + " gave up after " +
                                        p.HandshakeAttempts + " attempts.");
                                    p.Pending = null;
                                    p.GaveUp = true;
                                }
                                else
                                {
                                    if (p.HandshakeAttempts > 0 && p.HandshakeAttempts % 2 == 0)
                                        TryReResolveEndpoint(p);
                                    needInitiate = true;
                                }
                            }
                        }
                    }
                    if (needInitiate) InitiateHandshake(p); // re-locks _hsLock internally

                    Session sess = p.Session;
                    if (sess != null)
                    {
                        anyConnected = true;
                        double age = (now - p.SessionEstablished).TotalSeconds;

                        bool rekey = false;
                        lock (_hsLock)
                        {
                            if ((Timers.ShouldRekeyOnSend(age, 0) || sess.SendCounterExhausted) && p.Pending == null)
                            {
                                Log(WgSharp.Core.Logger.DebugMarker + "Session (peer " + p.Index + ") aging; initiating rekey.");
                                rekey = true;
                            }
                        }
                        if (rekey) InitiateHandshake(p);

                        if (Timers.SessionExpired(age))
                        {
                            Log("Session (peer " + p.Index + ") expired; dropping.");
                            Session old = p.Session;
                            p.Session = null;
                            if (old != null) try { old.Dispose(); } catch { }
                        }

                        double sinceSent = (now - p.LastSent).TotalSeconds;
                        double sinceRecv = (now - p.LastRecv).TotalSeconds;
                        int ka = p.PersistentKeepalive;
                        if (ka > 0 && sinceSent >= ka)
                            SendKeepalive(p);
                        else if (sinceRecv < Timers.KeepaliveTimeout && sinceSent >= Timers.KeepaliveTimeout)
                            SendKeepalive(p);
                    }
                }

                // Aggregate state for the UI: Connected if any peer has a session;
                // Failed if none connected and every dialing peer has given up;
                // otherwise Handshaking.
                bool allGaveUp = true, anyDialing = false;
                foreach (PeerState p in _peers)
                {
                    if (!p.HasEndpoint) continue;
                    anyDialing = true;
                    if (!p.GaveUp || p.Session != null) allGaveUp = false;
                }
                lock (_statusLock)
                {
                    if (anyConnected) _status.State = "Connected";
                    else if (anyDialing && allGaveUp) _status.State = "Failed";
                    else _status.State = "Handshaking";
                }
            }
        }

        private void SendKeepalive(PeerState p)
        {
            Session s = p.Session;
            if (s == null || p.Endpoint == null) return;
            try
            {
                byte[] empty = new byte[0];
                byte[] msg = s.Encrypt(empty, 0, 0);
                if (_awg) msg = AwgFraming.WrapTransport(msg, _cfg.EffectiveH4);
                _udp.SendTo(msg, msg.Length, p.Endpoint);
                p.LastSent = DateTime.UtcNow;
            }
            catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Keepalive error: " + ex.Message); }
        }
    }
}

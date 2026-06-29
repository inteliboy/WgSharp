using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using WgSharp.Tun;

namespace WgSharp.Core
{
    /// <summary>
    /// WireGuardNT-backed tunnel: drives the kernel-mode WireGuard data plane via
    /// wireguard.dll instead of our managed crypto/transport. Far higher throughput
    /// (kernel data path) at the cost of relying on the signed driver.
    ///
    /// wireguard.dll is loaded dynamically (LoadLibraryEx + GetProcAddress), per the
    /// WireGuardNT docs — it is NOT a static [DllImport] target, because its name
    /// and presence are runtime-determined. The function pointers are resolved into
    /// delegates.
    ///
    /// STATUS: functionally complete bring-up path — DLL loading, adapter
    /// lifecycle, the WIREGUARD_INTERFACE+PEER+ALLOWED_IP config blob, addressing
    /// (DNS/MTU/routes/interface metric), kill-switch integration, and a status
    /// poller are all implemented (see BuildConfigBlob, ApplyAddressing, PollLoop).
    /// What's NOT yet hardened, and why the Settings UI still flags this backend
    /// as a work in progress:
    ///   - Real-world throughput/stability under sustained data transfer hasn't
    ///     been validated on hardware — only connect/bring-up has been confirmed.
    ///   - Only single/first-peer-focused testing so far; multi-peer configs are
    ///     built correctly per the spec but untested end-to-end.
    /// (WireGuardCreateAdapter failing because a previous run didn't clean up is
    /// now recovered automatically via WireGuardOpenAdapter — see Start().)
    /// </summary>
    public sealed class WireGuardNtTunnel : ITunnelBackend
    {
        public event Action<string> LogMessage;
        private void Log(string m)
        {
            var h = LogMessage;
            if (h == null) return;
            h(WgSharp.Core.Logger.Tag(m, "WireGuardNT"));
        }

        private readonly Config _cfg;
        private readonly TunnelStatus _status = new TunnelStatus();
        private readonly object _statusLock = new object();

        private IntPtr _dll = IntPtr.Zero;
        private IntPtr _adapter = IntPtr.Zero;
        private ulong _luid;
        private bool _killSwitchEngaged;
        private Thread _poller;
        private Thread _pinger;
        // WireGuardNT's kernel driver handles the handshake internally, so a
        // true handshake RTT isn't available via this API. Latency is instead
        // measured live by PingLoop (periodic ICMP RTT to the endpoint).
        private volatile bool _running;

        public WireGuardNtTunnel(Config cfg) { _cfg = cfg; }

        // ---- native loader ----
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibraryExW(string lpLibFileName, IntPtr hFile, uint dwFlags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        // ---- WireGuardNT function delegates (subset needed for the data path) ----
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate IntPtr WireGuardCreateAdapterDelegate(string name, string tunnelType, ref Guid requestedGuid);
        [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
        private delegate IntPtr WireGuardOpenAdapterDelegate(string name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WireGuardCloseAdapterDelegate(IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void WireGuardGetAdapterLuidDelegate(IntPtr adapter, out ulong luid);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool WireGuardSetConfigurationDelegate(IntPtr adapter, IntPtr config, uint bytes);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool WireGuardGetConfigurationDelegate(IntPtr adapter, IntPtr config, ref uint bytes);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool WireGuardSetAdapterStateDelegate(IntPtr adapter, int state);

        private WireGuardCreateAdapterDelegate _create;
        private WireGuardOpenAdapterDelegate _open;
        private WireGuardCloseAdapterDelegate _close;
        private WireGuardGetAdapterLuidDelegate _getLuid;
        private WireGuardSetConfigurationDelegate _setConfig;
        private WireGuardGetConfigurationDelegate _getConfig;
        private WireGuardSetAdapterStateDelegate _setState;

        private const int WIREGUARD_ADAPTER_STATE_DOWN = 0;
        private const int WIREGUARD_ADAPTER_STATE_UP = 1;

        private static string BaseDir
        {
            get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        }

        private T Resolve<T>(string name) where T : class
        {
            IntPtr p = GetProcAddress(_dll, name);
            if (p == IntPtr.Zero)
                throw new Exception("wireguard.dll is missing export " + name + ".");
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        private void LoadDll()
        {
            string path = Path.Combine(BaseDir, "wireguard.dll");
            if (!File.Exists(path))
                throw new Exception("wireguard.dll not found next to the executable. " +
                    "Enable the WireGuardNT backend only after the driver has been downloaded at startup.");

            _dll = LoadLibraryExW(path, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
            if (_dll == IntPtr.Zero)
                throw new Exception("Failed to load wireguard.dll (error " +
                    Marshal.GetLastWin32Error() + "). Is it the correct architecture and signed?");

            _create = Resolve<WireGuardCreateAdapterDelegate>("WireGuardCreateAdapter");
            _open = Resolve<WireGuardOpenAdapterDelegate>("WireGuardOpenAdapter");
            _close = Resolve<WireGuardCloseAdapterDelegate>("WireGuardCloseAdapter");
            _getLuid = Resolve<WireGuardGetAdapterLuidDelegate>("WireGuardGetAdapterLUID");
            _setConfig = Resolve<WireGuardSetConfigurationDelegate>("WireGuardSetConfiguration");
            _getConfig = Resolve<WireGuardGetConfigurationDelegate>("WireGuardGetConfiguration");
            _setState = Resolve<WireGuardSetAdapterStateDelegate>("WireGuardSetAdapterState");
            Log(WgSharp.Core.Logger.DebugMarker + "WireGuardNT loaded; functions resolved.");
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            lock (_statusLock) { _status.State = "Starting"; _status.Endpoint = _cfg.Endpoint; }

            LoadDll();

            // Create the adapter with a deterministic GUID (same scheme as the
            // managed backend would use), so Windows network categorization stays
            // stable across reconnects.
            // Distinct seed from the managed (Wintun) backend's adapter GUID is
            // deliberate: the two backends use fundamentally different drivers
            // (Wintun vs WireGuardNT's miniport), so reusing the same GUID would
            // ask Windows to treat a different underlying device as the same
            // network identity. That breaks NLA's network-profile matching and can
            // make Windows re-run "new network" classification when switching
            // backends — which re-prompts the firewall consent dialog even though
            // the exe itself hasn't changed.
            Guid guid = DeterministicGuid("WgSharp-WireGuardNT");
            _adapter = _create("WgSharp", "WgSharp", ref guid);
            if (_adapter == IntPtr.Zero)
            {
                int createErr = Marshal.GetLastWin32Error();
                // A previous run that didn't shut down cleanly (crash, force-kill)
                // can leave an adapter of this name/GUID already registered, which
                // makes WireGuardCreateAdapter fail. Recover by opening the existing
                // adapter instead of requiring the user to reboot.
                Log("WireGuardCreateAdapter failed (error " + createErr +
                    "); trying to open an existing adapter of the same name\u2026");
                _adapter = _open("WgSharp");
                if (_adapter == IntPtr.Zero)
                    throw new Exception("WireGuardCreateAdapter failed (error " + createErr +
                        ") and no existing adapter could be opened either (error " +
                        Marshal.GetLastWin32Error() + "). Are you running elevated?");
                Log(WgSharp.Core.Logger.DebugMarker + "Opened existing WireGuardNT adapter (reused from a previous session).");
            }

            ulong luid;
            _getLuid(_adapter, out luid);
            _luid = luid;
            Log(WgSharp.Core.Logger.DebugMarker + "WireGuardNT adapter up (LUID=" + luid + ").");

            // Build the WIREGUARD_INTERFACE + peers + allowed-IPs blob and hand it
            // to the driver, then apply addressing and bring the adapter up.
            int blobLen;
            IntPtr blob = BuildConfigBlob(out blobLen);
            try
            {
                if (!_setConfig(_adapter, blob, (uint)blobLen))
                    throw new Exception("WireGuardSetConfiguration failed (error " +
                        Marshal.GetLastWin32Error() + ").");
            }
            finally { Marshal.FreeHGlobal(blob); }
            Log(WgSharp.Core.Logger.DebugMarker + "WireGuardNT configuration applied (" + _cfg.Peers.Count + " peer(s)).");

            ApplyAddressing(luid);

            if (!_setState(_adapter, WIREGUARD_ADAPTER_STATE_UP))
                throw new Exception("WireGuardSetAdapterState(UP) failed (error " +
                    Marshal.GetLastWin32Error() + ").");
            Log(WgSharp.Core.Logger.DebugMarker + "WireGuardNT adapter state UP.");

            lock (_statusLock) _status.State = "Handshaking";

            // Status poller: poll WireGuardGetConfiguration for handshake time and
            // byte counters and surface them in the UI.
            _poller = new Thread(PollLoop) { IsBackground = true, Name = "wg-nt-poll" };
            _poller.Start();

            // Latency: the kernel driver runs the handshake internally, so we
            // can't time a handshake RTT the way the managed backend does. The
            // honest, live alternative is to periodically ICMP-ping the peer
            // endpoint and report that round-trip — a real, fluctuating figure,
            // unlike the old "time to first handshake" proxy which was measured
            // once and then sat static. See PingLoop.
            _pinger = new Thread(PingLoop) { IsBackground = true, Name = "wg-nt-ping" };
            _pinger.Start();
        }

        // ===========================================================
        //  Config blob marshalling (WIREGUARD_INTERFACE + peers + allowed IPs)
        // ===========================================================

        // Struct sizes and field offsets (x64, all ALIGNED(8)), computed from
        // api/wireguard.h. Writing raw bytes at explicit offsets is safer here than
        // StructLayout, since the whole blob is one contiguous manual memory walk.
        private const int IF_SIZE = 80;
        private const int IF_Flags = 0, IF_ListenPort = 4, IF_PrivateKey = 6, IF_PublicKey = 38, IF_PeersCount = 72;
        private const int PEER_SIZE = 136;
        private const int PEER_Flags = 0, PEER_Reserved = 4, PEER_PublicKey = 8, PEER_PresharedKey = 40;
        private const int PEER_Keepalive = 72, PEER_Endpoint = 76, PEER_Tx = 104, PEER_Rx = 112, PEER_Last = 120, PEER_AllowedCount = 128;
        private const int AIP_SIZE = 24;
        private const int AIP_Address = 0, AIP_Family = 16, AIP_Cidr = 18, AIP_Flags = 20;

        // Interface flags.
        private const uint IF_HAS_PUBLIC_KEY = 1 << 0;
        private const uint IF_HAS_PRIVATE_KEY = 1 << 1;
        private const uint IF_HAS_LISTEN_PORT = 1 << 2;
        private const uint IF_REPLACE_PEERS = 1 << 3;
        // Peer flags.
        private const uint PEER_HAS_PUBLIC_KEY = 1 << 0;
        private const uint PEER_HAS_PRESHARED_KEY = 1 << 1;
        private const uint PEER_HAS_KEEPALIVE = 1 << 2;
        private const uint PEER_HAS_ENDPOINT = 1 << 3;
        private const uint PEER_REPLACE_ALLOWED_IPS = 1 << 5;

        private const ushort AF_INET = 2;
        private const ushort AF_INET6 = 23;

        private IntPtr BuildConfigBlob(out int totalLen)
        {
            // Count allowed-IPs across all peers to size the blob.
            int peerCount = _cfg.Peers.Count;
            int aipTotal = 0;
            foreach (Config.Peer p in _cfg.Peers)
                aipTotal += CountValidAllowedIps(p.AllowedIPs);

            totalLen = IF_SIZE + peerCount * PEER_SIZE + aipTotal * AIP_SIZE;
            IntPtr blob = Marshal.AllocHGlobal(totalLen);

            // Zero the whole block first (Reserved fields, padding, unused union bytes).
            for (int i = 0; i < totalLen; i++) Marshal.WriteByte(blob, i, 0);

            // ---- WIREGUARD_INTERFACE ----
            uint ifFlags = IF_HAS_PRIVATE_KEY | IF_REPLACE_PEERS;
            if (_cfg.ListenPort > 0) ifFlags |= IF_HAS_LISTEN_PORT;
            Marshal.WriteInt32(blob, IF_Flags, unchecked((int)ifFlags));
            Marshal.WriteInt16(blob, IF_ListenPort, unchecked((short)(ushort)_cfg.ListenPort));
            WriteBytes(blob, IF_PrivateKey, _cfg.PrivateKey, 32);
            // PublicKey is unused on set; leave zero.
            Marshal.WriteInt32(blob, IF_PeersCount, peerCount);

            int off = IF_SIZE;
            foreach (Config.Peer p in _cfg.Peers)
            {
                int peerBase = off;
                uint peerFlags = PEER_REPLACE_ALLOWED_IPS;
                if (p.PublicKey != null) peerFlags |= PEER_HAS_PUBLIC_KEY;
                if (p.PresharedKey != null) peerFlags |= PEER_HAS_PRESHARED_KEY;
                if (p.PersistentKeepalive > 0) peerFlags |= PEER_HAS_KEEPALIVE;

                IPEndPoint ep = TryResolve(p.Endpoint);
                if (ep != null) peerFlags |= PEER_HAS_ENDPOINT;

                Marshal.WriteInt32(blob, peerBase + PEER_Flags, unchecked((int)peerFlags));
                if (p.PublicKey != null) WriteBytes(blob, peerBase + PEER_PublicKey, p.PublicKey, 32);
                if (p.PresharedKey != null) WriteBytes(blob, peerBase + PEER_PresharedKey, p.PresharedKey, 32);
                if (p.PersistentKeepalive > 0)
                    Marshal.WriteInt16(blob, peerBase + PEER_Keepalive, unchecked((short)(ushort)p.PersistentKeepalive));
                if (ep != null) WriteEndpoint(blob, peerBase + PEER_Endpoint, ep);

                int aipCount = CountValidAllowedIps(p.AllowedIPs);
                Marshal.WriteInt32(blob, peerBase + PEER_AllowedCount, aipCount);

                off += PEER_SIZE;

                // ---- WIREGUARD_ALLOWED_IP entries for this peer ----
                foreach (string cidr in p.AllowedIPs)
                {
                    IPAddress addr; int prefix;
                    if (!WgSharp.Tun.AdapterConfig.TryParseCidr(cidr, out addr, out prefix)) continue;
                    bool v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
                    byte[] raw = addr.GetAddressBytes();
                    WriteBytes(blob, off + AIP_Address, raw, raw.Length); // 4 or 16 bytes
                    Marshal.WriteInt16(blob, off + AIP_Family, unchecked((short)(v6 ? AF_INET6 : AF_INET)));
                    Marshal.WriteByte(blob, off + AIP_Cidr, (byte)prefix);
                    // Flags = 0 (add).
                    off += AIP_SIZE;
                }
            }

            return blob;
        }

        private static int CountValidAllowedIps(System.Collections.Generic.List<string> list)
        {
            int n = 0;
            foreach (string cidr in list)
            {
                IPAddress a; int p;
                if (WgSharp.Tun.AdapterConfig.TryParseCidr(cidr, out a, out p)) n++;
            }
            return n;
        }

        // Write a SOCKADDR_INET into the 28-byte endpoint slot: family (LE) at +0,
        // port (BIG-ENDIAN) at +2, then the address.
        private static void WriteEndpoint(IntPtr blob, int at, IPEndPoint ep)
        {
            bool v6 = ep.AddressFamily == AddressFamily.InterNetworkV6;
            Marshal.WriteInt16(blob, at + 0, unchecked((short)(v6 ? AF_INET6 : AF_INET)));
            // Port in network byte order (big-endian).
            Marshal.WriteByte(blob, at + 2, (byte)((ep.Port >> 8) & 0xFF));
            Marshal.WriteByte(blob, at + 3, (byte)(ep.Port & 0xFF));
            byte[] addr = ep.Address.GetAddressBytes();
            if (v6)
            {
                // sin6_flowinfo (4) stays zero at +4; sin6_addr (16) at +8.
                WriteBytes(blob, at + 8, addr, 16);
                // sin6_scope_id (4) at +24 stays zero.
            }
            else
            {
                // sin_addr (4) at +4; remaining 8 bytes of sin_zero stay zero.
                WriteBytes(blob, at + 4, addr, 4);
            }
        }

        private static void WriteBytes(IntPtr blob, int at, byte[] src, int count)
        {
            for (int i = 0; i < count; i++) Marshal.WriteByte(blob, at + i, src[i]);
        }

        private IPEndPoint TryResolve(string spec)
        {
            if (string.IsNullOrEmpty(spec)) return null;
            try { return WgSharp.Net.UdpTransport.ResolveEndpoint(spec); }
            catch { return null; }
        }

        // Apply IP address, DNS, MTU, and routes to the adapter using the same
        // backend-agnostic AdapterConfig helpers the managed tunnel uses.
        private void ApplyAddressing(ulong luid)
        {
            WgSharp.Tun.AdapterConfig.Log += Log;

            // Interface address.
            IPAddress ifAddr; int ifPrefix;
            if (WgSharp.Tun.AdapterConfig.TryParseCidr(_cfg.Address, out ifAddr, out ifPrefix))
            {
                WgSharp.Tun.AdapterConfig.SetAddress(luid, ifAddr, ifPrefix);
                Log(WgSharp.Core.Logger.DebugMarker + "Address " + _cfg.Address + " set via API.");
            }

            // MTU (explicit or auto).
            try { WgSharp.Tun.AdapterConfig.SetMtu(luid, _cfg.Mtu); }
            catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "MTU set skipped: " + ex.Message); }

            // Routes from each peer's AllowedIPs.
            foreach (Config.Peer p in _cfg.Peers)
            {
                foreach (string cidr in p.AllowedIPs)
                {
                    IPAddress dest; int prefix;
                    if (!WgSharp.Tun.AdapterConfig.TryParseCidr(cidr, out dest, out prefix)) continue;
                    try { WgSharp.Tun.AdapterConfig.AddRouteForAllowedIp(luid, dest, prefix); }
                    catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "Route " + cidr + " skipped: " + ex.Message); }
                }
            }

            // Force a low, fixed interface metric (see managed Tunnel for why a
            // per-route metric alone isn't always sufficient).
            WgSharp.Tun.AdapterConfig.SetInterfaceMetric(luid, 1);
            WgSharp.Tun.AdapterConfig.DumpRouteDiagnostics(luid);

            // DNS.
            try
            {
                System.Collections.Generic.List<string> servers = _cfg.DnsServers();
                System.Collections.Generic.List<string> domains = _cfg.DnsSearchDomains();
                if (servers.Count > 0 || domains.Count > 0)
                    WgSharp.Tun.AdapterConfig.SetDns(luid, servers, domains);
            }
            catch (Exception ex) { Log(WgSharp.Core.Logger.DebugMarker + "DNS set skipped: " + ex.Message); }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_poller != null) _poller.Join(1500);
            // PingLoop also watches _running and sleeps in small slices, so it
            // exits promptly; PingTimeoutMs bounds a ping already in flight.
            if (_pinger != null) _pinger.Join(PingTimeoutMs + 1000);

            // Disengage the kill-switch first so connectivity is restored even if
            // adapter teardown lags.
            if (_killSwitchEngaged)
            {
                try { WgSharp.Tun.KillSwitch.Disengage(); }
                catch (Exception ex) { Log("Kill-switch disengage error: " + ex.Message); }
                _killSwitchEngaged = false;
            }

            try
            {
                if (_adapter != IntPtr.Zero && _setState != null)
                    _setState(_adapter, WIREGUARD_ADAPTER_STATE_DOWN);
            }
            catch (Exception ex) { Log("WireGuardNT set-down error: " + ex.Message); }

            try
            {
                if (_adapter != IntPtr.Zero && _close != null)
                    _close(_adapter);
            }
            catch (Exception ex) { Log("WireGuardNT close error: " + ex.Message); }
            _adapter = IntPtr.Zero;

            if (_dll != IntPtr.Zero) { try { FreeLibrary(_dll); } catch { } _dll = IntPtr.Zero; }
            lock (_statusLock) _status.State = "Idle";
            Log("WireGuardNT tunnel stopped.");
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

        private void PollLoop()
        {
            // Reusable buffer; grows if the driver reports it needs more.
            int cap = IF_SIZE + 8 * (PEER_SIZE + 16 * AIP_SIZE);
            IntPtr buf = Marshal.AllocHGlobal(cap);
            try
            {
                while (_running)
                {
                    Thread.Sleep(1000);
                    if (!_running) break;
                    try { PollOnce(ref buf, ref cap); }
                    catch { /* transient; try again next tick */ }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void PollOnce(ref IntPtr buf, ref int cap)
        {
            uint bytes = (uint)cap;
            bool ok = _getConfig(_adapter, buf, ref bytes);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                const int ERROR_MORE_DATA = 234;
                if (err == ERROR_MORE_DATA && bytes > cap)
                {
                    Marshal.FreeHGlobal(buf);
                    cap = (int)bytes;
                    buf = Marshal.AllocHGlobal(cap);
                    bytes = (uint)cap;
                    if (!_getConfig(_adapter, buf, ref bytes)) return;
                }
                else return;
            }

            // Parse: interface header, then peers. Sum Tx/Rx and take the most
            // recent handshake across peers.
            int peerCount = Marshal.ReadInt32(buf, IF_PeersCount);
            int off = IF_SIZE;
            ulong tx = 0, rx = 0, lastHs = 0;
            for (int i = 0; i < peerCount; i++)
            {
                if (off + PEER_SIZE > (int)bytes) break;
                tx += (ulong)Marshal.ReadInt64(buf, off + PEER_Tx);
                rx += (ulong)Marshal.ReadInt64(buf, off + PEER_Rx);
                ulong hs = (ulong)Marshal.ReadInt64(buf, off + PEER_Last);
                if (hs > lastHs) lastHs = hs;
                int aip = Marshal.ReadInt32(buf, off + PEER_AllowedCount);
                off += PEER_SIZE + aip * AIP_SIZE;
            }

            DateTime hsTime = DateTime.MinValue;
            DateTime hsTimeUtc = DateTime.MinValue;
            bool handshaked = lastHs != 0;
            if (handshaked)
            {
                // 100ns intervals since 1601-01-01 UTC == Windows FILETIME.
                try { hsTimeUtc = DateTime.FromFileTimeUtc((long)lastHs); hsTime = hsTimeUtc.ToLocalTime(); }
                catch { hsTime = DateTime.MinValue; hsTimeUtc = DateTime.MinValue; }
            }

            lock (_statusLock)
            {
                _status.TxBytes = (long)tx;
                _status.RxBytes = (long)rx;
                _status.LastHandshakeTime = hsTime;
                _status.State = handshaked ? "Connected" : "Handshaking";
            }

            // Latency is now measured live by PingLoop (periodic ICMP RTT to
            // the endpoint), not derived from the one-time handshake delay, so
            // there's nothing to compute here.

            // Engage the kill-switch once, after the first successful handshake
            // (same deferral the managed backend uses so the handshake itself isn't
            // blocked). Only when the config requests it.
            if (handshaked && !_killSwitchEngaged && _cfg.BlockUntunneled)
            {
                _killSwitchEngaged = true;
                try
                {
                    IPEndPoint ep = TryResolve(_cfg.Endpoint);
                    string epIp = ep != null ? ep.Address.ToString() : null;
                    WgSharp.Tun.KillSwitch.Engage(epIp, _cfg.Address, _luid);
                }
                catch (Exception ex) { Log("Kill-switch engage error: " + ex.Message); }
            }
        }

        // ===========================================================
        //  Live latency: periodic ICMP ping THROUGH the tunnel
        // ===========================================================
        // The kernel driver runs the handshake internally, so we can't time a
        // handshake RTT. Instead we periodically ICMP-ping a target that lives
        // INSIDE the tunnel and report that round-trip — i.e. true tunnel
        // latency, a real, continuously-updating figure (the old one-shot
        // handshake-time estimate never moved).
        //
        // Pinging the server's PUBLIC endpoint is unreliable: most WireGuard
        // servers don't answer ICMP on their public address, and with a
        // full-tunnel config that traffic is deliberately pinned OUTSIDE the
        // tunnel anyway — so it measured the wrong thing even when it worked.
        // BuildTunnelTargets instead produces an ordered list of in-tunnel
        // candidates (config DNS IPs, the .1 gateway of the interface subnet,
        // our own interface address, the .1 of each specific AllowedIPs network,
        // then the public endpoint last). PingLoop tries the current one and
        // rotates to the next if it keeps failing, so a non-responding target
        // no longer leaves latency permanently blank — the whole point of the
        // fallback. A fresh Ping is created each cycle, since a Ping whose Send
        // threw can stay unusable.
        private const int PingIntervalMs = 5000;
        private const int PingTimeoutMs = 2000;

        private void PingLoop()
        {
            // Settle delay before the first ping: the adapter's addresses and
            // routes are applied right around connect time, and pinging too
            // early (before the route table catches up) is what made the first
            // probe throw immediately rather than time out. ~4s of slack.
            for (int i = 0; i < 20 && _running; i++) Thread.Sleep(200);

            // Build the ordered candidate list ONCE. Each cycle we try targets
            // in priority order and use the FIRST that answers — so the real
            // tunnel gateway (e.g. 10.9.0.1) is always preferred over the
            // ~0 ms self-ping fallback whenever it's reachable. We don't "lock
            // onto" a winner: re-probing from the top each time means a target
            // that recovers is picked up again, and a target that dies is
            // skipped, without ever getting stuck on the wrong one.
            System.Collections.Generic.List<IPAddress> candidates = BuildTunnelTargets();
            if (candidates.Count == 0)
            {
                Log(WgSharp.Core.Logger.DebugMarker + "No tunnel latency probe target could be derived.");
                return;
            }
            var names = new System.Text.StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                if (i > 0) names.Append(", ");
                names.Append(candidates[i].ToString());
            }
            Log(WgSharp.Core.Logger.DebugMarker + "Tunnel latency probe candidates: " + names);

            IPAddress lastWinner = null;

            while (_running)
            {
                long ms = -1;
                IPAddress winner = null;

                // Try each candidate in priority order; stop at the first that
                // answers. The first candidate (highest priority real target)
                // that succeeds wins, so we never settle for self-ping while a
                // real host is up.
                for (int i = 0; i < candidates.Count && _running; i++)
                {
                    IPAddress target = candidates[i];
                    try
                    {
                        // A fresh Ping each attempt: a Ping whose previous Send
                        // faulted can stay unusable, and PingException tends to
                        // recur on the same instance.
                        using (var ping = new System.Net.NetworkInformation.Ping())
                        {
                            byte[] payload = System.Text.Encoding.ASCII.GetBytes("WgSharp-probe");
                            var reply = ping.Send(target, PingTimeoutMs, payload);
                            if (reply != null &&
                                reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                            {
                                ms = reply.RoundtripTime;
                                winner = target;
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // PingException wraps the real cause (often a
                        // Win32Exception); surface the inner message too.
                        string detail = ex.Message;
                        if (ex.InnerException != null) detail += " -> " + ex.InnerException.Message;
                        Log(WgSharp.Core.Logger.DebugMarker + "Tunnel latency probe error for " +
                            target + ": " + detail);
                    }
                }

                lock (_statusLock) _status.LatencyMs = winner != null ? ms : -1;

                // Log only when the responding target CHANGES, so the Log tab
                // isn't spammed every interval but still records fallbacks.
                if (winner != null && !winner.Equals(lastWinner))
                {
                    Log(WgSharp.Core.Logger.DebugMarker + "Tunnel latency now measured against " + winner + ".");
                    lastWinner = winner;
                }

                // Sleep in small slices so Stop() is responsive.
                for (int slept = 0; slept < PingIntervalMs && _running; slept += 200)
                    Thread.Sleep(200);
            }
        }

        /// <summary>
        /// Builds an ordered, de-duplicated list of candidate addresses to ping
        /// for tunnel latency, most-likely-to-respond first. PingLoop tries them
        /// in order and rotates past any that don't answer. Priority:
        ///   1) configured DNS server IPs (usually in-tunnel and pingable),
        ///   2) the gateway (.1) of the interface subnet,
        ///   3) the interface's own address (the local tunnel IP always
        ///      responds, giving a near-zero but non-blank reading that at least
        ///      confirms the adapter is up),
        ///   4) the .1 of each specific (non-default) AllowedIPs network,
        ///   5) the public endpoint as a last resort.
        /// </summary>
        private System.Collections.Generic.List<IPAddress> BuildTunnelTargets()
        {
            var list = new System.Collections.Generic.List<IPAddress>();
            System.Action<IPAddress> add = delegate(IPAddress a)
            {
                if (a == null) return;
                foreach (IPAddress e in list) if (e.Equals(a)) return; // de-dup
                list.Add(a);
            };

            IPAddress ifAddr = null; int ifPrefix = 32;
            try { WgSharp.Tun.AdapterConfig.TryParseCidr(_cfg.Address, out ifAddr, out ifPrefix); }
            catch { ifAddr = null; }

            // 1) DNS server IPs from the config — usually in-tunnel and pingable.
            try
            {
                foreach (string s in _cfg.DnsServers())
                {
                    IPAddress dns;
                    if (IPAddress.TryParse(s, out dns) &&
                        dns.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        add(dns);
                }
            }
            catch { }

            // 2) The tunnel gateway, i.e. the peer's own tunnel address — this
            //    is what actually answers (your manual `ping 10.9.0.1`). WireGuard
            //    client configs almost always set Address with a /32 (e.g.
            //    10.9.0.6/32), so the interface's own prefix tells us NOTHING
            //    about the real subnet — deriving ".1 of /32" gives a bogus host.
            //    Instead we take ".1" of the address's natural /24, which is the
            //    near-universal convention for the server/gateway side.
            if (ifAddr != null && ifAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                IPAddress gw24 = FirstHostOfSubnet(ifAddr, 24);
                if (gw24 != null && !gw24.Equals(ifAddr)) add(gw24);

                // If the interface DID carry a real (non-/32) prefix, also try
                // the gateway of that actual subnet.
                if (ifPrefix > 0 && ifPrefix < 32)
                {
                    IPAddress gw = FirstHostOfSubnet(ifAddr, ifPrefix);
                    if (gw != null && !gw.Equals(ifAddr)) add(gw);
                }
            }

            // 3) The .1 of each specific (non-default) IPv4 AllowedIPs network —
            //    another path to the peer side for split-tunnel configs.
            try
            {
                foreach (Config.Peer p in _cfg.Peers)
                {
                    foreach (string cidr in p.AllowedIPs)
                    {
                        IPAddress dest; int prefix;
                        if (!WgSharp.Tun.AdapterConfig.TryParseCidr(cidr, out dest, out prefix)) continue;
                        if (dest.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                        if (prefix == 0) continue; // skip default route 0.0.0.0/0
                        add(FirstHostOfSubnet(dest, prefix));
                    }
                }
            }
            catch { }

            // 4) Public endpoint — sometimes answers, real-ish RTT if it does.
            try
            {
                IPEndPoint ep = TryResolve(_cfg.Endpoint);
                if (ep != null) add(ep.Address);
            }
            catch { }

            // 5) LAST RESORT ONLY: our own interface address. It always answers
            //    in ~0 ms, so it must come AFTER every real target — otherwise a
            //    meaningless 0 ms masks the true tunnel latency. Including it at
            //    all just guarantees the field isn't permanently blank for a
            //    config where nothing else responds to ICMP.
            if (ifAddr != null && ifAddr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                add(ifAddr);

            return list;
        }

        /// <summary>First usable host (.1) of the IPv4 subnet containing addr.</summary>
        private static IPAddress FirstHostOfSubnet(IPAddress addr, int prefixLen)
        {
            byte[] b = addr.GetAddressBytes();
            if (b.Length != 4) return null;
            uint val = ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
            uint mask = prefixLen == 0 ? 0u : (prefixLen >= 32 ? 0xFFFFFFFFu : ~((1u << (32 - prefixLen)) - 1));
            uint network = val & mask;
            uint first = network + 1;       // .1 of the subnet
            return new IPAddress(new byte[]
            {
                (byte)(first >> 24), (byte)(first >> 16), (byte)(first >> 8), (byte)first
            });
        }

        // Same deterministic RFC 4122 v5 GUID scheme used elsewhere, so the adapter
        // identity is stable across reconnects.
        private static Guid DeterministicGuid(string name)
        {
            byte[] ns = new byte[]
            {
                0x6b, 0xa7, 0xc1, 0x10, 0x9d, 0xad, 0x11, 0xd1,
                0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8
            };
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name == null ? "" : name);
            byte[] input = new byte[ns.Length + nameBytes.Length];
            Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
            Buffer.BlockCopy(nameBytes, 0, input, ns.Length, nameBytes.Length);
            byte[] hash;
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
                hash = sha1.ComputeHash(input);
            byte[] g = new byte[16];
            Array.Copy(hash, 0, g, 0, 16);
            g[6] = (byte)((g[6] & 0x0F) | 0x50);
            g[8] = (byte)((g[8] & 0x3F) | 0x80);
            Swap(g, 0, 3); Swap(g, 1, 2); Swap(g, 4, 5); Swap(g, 6, 7);
            return new Guid(g);
        }

        private static void Swap(byte[] b, int i, int j) { byte t = b[i]; b[i] = b[j]; b[j] = t; }
    }
}

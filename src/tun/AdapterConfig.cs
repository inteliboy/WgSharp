using System;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WgSharp.Tun
{
    /// <summary>
    /// Assigns the tunnel interface address and installs routes for the peer's
    /// AllowedIPs. Tries the IP Helper API (iphlpapi.dll) first; on any failure
    /// falls back to shelling out to netsh, which is slower but very robust.
    ///
    /// The adapter is identified by its NET_LUID (obtained from Wintun). All
    /// changes are non-persistent and disappear when the adapter is closed.
    /// </summary>
    public static class AdapterConfig
    {
        public static event Action<string> Log;
        // AdapterConfig only ever emits low-level setup detail (per-address,
        // per-route, per-DNS, metric, and the route-diagnostics dump). All of
        // it is diagnostic noise for normal use, so it goes out marked debug —
        // it appears in the Log tab only when "Debug log" is enabled. Genuine
        // hard failures aren't swallowed here; they surface as exceptions to
        // the activate path, which logs them as meaningful errors.
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.DebugMarker + "[AdapterConfig] " + m); }

        // ---------------- public entry points ----------------

        /// <summary>Assign an address (IPv4 via API/netsh, IPv6 via netsh) to the adapter.</summary>
        public static void SetAddress(ulong luid, IPAddress addr, int prefixLen)
        {
            bool v6 = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (v6)
            {
                // The IP Helper API path here is IPv4-only; use netsh for IPv6,
                // which handles it cleanly.
                SetAddressNetsh(luid, addr, prefixLen);
                return;
            }
            try
            {
                if (TrySetAddressApi(luid, addr, prefixLen)) { L("Address " + addr + "/" + prefixLen + " set via API."); return; }
            }
            catch (Exception ex) { L("Address API failed (" + ex.Message + "); trying netsh."); }

            SetAddressNetsh(luid, addr, prefixLen);
        }

        /// <summary>Add a route (IPv4 via API/netsh, IPv6 via netsh) out of this adapter.</summary>
        // Install a route for an AllowedIPs entry, splitting a literal full-tunnel
        // default (0.0.0.0/0 or ::/0) into two /1 routes instead of installing it
        // as a single /0. This is the trick the official client uses: a /1 route is
        // MORE SPECIFIC than the physical adapter's existing 0.0.0.0/0 route, so
        // longest-prefix-match makes the tunnel win unconditionally — sidestepping
        // metric ties entirely (which a single 0.0.0.0/0 vs 0.0.0.0/0 comparison is
        // vulnerable to: equal metrics tie, and Windows' tie-break can still favor
        // the physical adapter). Non-default prefixes are installed unchanged.
        public static void AddRouteForAllowedIp(ulong luid, IPAddress dest, int prefixLen)
        {
            bool v6 = dest.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            bool isFullTunnelDefault = prefixLen == 0 &&
                (v6 ? dest.Equals(IPAddress.IPv6Any) : dest.Equals(IPAddress.Any));

            if (!isFullTunnelDefault) { AddRoute(luid, dest, prefixLen); return; }

            if (!v6)
            {
                L("Splitting 0.0.0.0/0 into 0.0.0.0/1 + 128.0.0.0/1 (guarantees the tunnel " +
                  "wins via longest-prefix-match, regardless of any default-route metric tie).");
                AddRoute(luid, IPAddress.Parse("0.0.0.0"), 1);
                AddRoute(luid, IPAddress.Parse("128.0.0.0"), 1);
            }
            else
            {
                L("Splitting ::/0 into ::/1 + 8000::/1 (same longest-prefix-match guarantee for IPv6).");
                AddRoute(luid, IPAddress.Parse("::"), 1);
                AddRoute(luid, IPAddress.Parse("8000::"), 1);
            }
        }

        public static void AddRoute(ulong luid, IPAddress dest, int prefixLen)
        {
            bool v6 = dest.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (v6)
            {
                AddRouteNetsh(luid, dest, prefixLen);
                return;
            }

            // A route destination must be the network base address, not a host
            // inside it. e.g. 10.0.0.2/24 is invalid as a prefix and is rejected
            // by both the API (error 87) and netsh; mask it to 10.0.0.0/24.
            IPAddress masked = MaskToNetwork(dest, prefixLen);
            if (!masked.Equals(dest))
                L("Route " + dest + "/" + prefixLen + " masked to network " + masked + "/" + prefixLen + ".");
            dest = masked;

            try
            {
                if (TryAddRouteApi(luid, dest, prefixLen)) { L("Route " + dest + "/" + prefixLen + " added via API."); return; }
            }
            catch (Exception ex) { L("Route API failed (" + ex.Message + "); trying netsh."); }

            AddRouteNetsh(luid, dest, prefixLen);
        }

        private static IPAddress MaskToNetwork(IPAddress addr, int prefixLen)
        {
            byte[] b = addr.GetAddressBytes();
            uint a = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
            uint mask = prefixLen == 0 ? 0u : 0xFFFFFFFFu << (32 - prefixLen);
            uint net = a & mask;
            return new IPAddress(new byte[]
            {
                (byte)(net >> 24), (byte)(net >> 16), (byte)(net >> 8), (byte)net
            });
        }

        /// <summary>
        /// Find the system's current default-gateway IP (the physical one), so we
        /// can pin a host route to the VPN endpoint through it. Returns null if no
        /// suitable gateway is found. Excludes the tunnel adapter itself.
        /// </summary>
        public static IPAddress FindDefaultGateway(string excludeAdapterDescription)
        {
            IPAddress best = null;
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                // skip the tunnel adapter
                if (!string.IsNullOrEmpty(excludeAdapterDescription) &&
                    ni.Description != null &&
                    ni.Description.IndexOf(excludeAdapterDescription, StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;
                if (ni.Description != null &&
                    ni.Description.IndexOf("WgSharp", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address == null) continue;
                    if (gw.Address.AddressFamily != AddressFamily.InterNetwork) continue; // v4 gw
                    if (gw.Address.Equals(IPAddress.Any)) continue;
                    best = gw.Address;
                    break;
                }
                if (best != null) break;
            }
            return best;
        }

        /// <summary>
        /// Pin a /32 host route to the VPN endpoint via the physical default
        /// gateway, so the encrypted tunnel packets to the server don't get routed
        /// back into the tunnel (which would blackhole the handshake when
        /// AllowedIPs is 0.0.0.0/0). Uses netsh for simplicity and robustness.
        /// </summary>
        public static bool PinEndpointRoute(IPAddress endpoint, IPAddress gateway)
        {
            if (endpoint == null || gateway == null) return false;
            if (endpoint.AddressFamily != AddressFamily.InterNetwork) return false; // v4 only here
            try
            {
                // Find the interface index that owns this gateway.
                int ifIndex = FindInterfaceIndexForGateway(gateway);
                // netsh syntax: add route <prefix> <interface> nexthop=<gw> ...
                // The interface token is required when specifying a nexthop.
                if (ifIndex <= 0)
                {
                    L("Could not resolve interface for gateway " + gateway + "; endpoint route not pinned.");
                    return false;
                }
                // Remove any stale route to this destination first (a previous
                // session may have left one), so 'add' doesn't fail with
                // "object already exists".
                try { RunNetsh("interface ipv4 delete route " + endpoint + "/32 " + ifIndex); }
                catch { /* ignore if absent */ }

                RunNetsh("interface ipv4 add route " + endpoint + "/32 " + ifIndex +
                         " nexthop=" + gateway + " metric=1 store=active");
                L("Pinned endpoint host route " + endpoint + "/32 via " + gateway +
                  " (if " + ifIndex + ").");
                return true;
            }
            catch (Exception ex)
            {
                L("Could not pin endpoint route: " + ex.Message);
                return false;
            }
        }

        public static void UnpinEndpointRoute(IPAddress endpoint)
        {
            if (endpoint == null) return;
            try { RunNetsh("interface ipv4 delete route " + endpoint + "/32"); }
            catch { /* best-effort */ }
        }

        private static int FindInterfaceIndexForGateway(IPAddress gateway)
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                IPInterfaceProperties props = ni.GetIPProperties();
                foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                {
                    if (gw.Address != null && gw.Address.Equals(gateway))
                    {
                        try { return props.GetIPv4Properties().Index; }
                        catch { return -1; }
                    }
                }
            }
            return -1;
        }

        // ---------------- IP Helper API path ----------------

        // SOCKADDR_INET as a fixed 28-byte blob; we only fill the IPv4 view.
        [StructLayout(LayoutKind.Sequential)]
        private struct SockAddrInet
        {
            public ushort si_family;   // AF_INET = 2
            public ushort sin_port;
            public uint sin_addr;      // IPv4 address, network byte order
            // padding to sizeof(SOCKADDR_IN6) = 28 bytes total
            public ulong pad0;
            public ulong pad1;
            public uint pad2;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibUnicastIpAddressRow
        {
            public SockAddrInet Address;
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public int PrefixOrigin;
            public int SuffixOrigin;
            public uint ValidLifetime;
            public uint PreferredLifetime;
            public byte OnLinkPrefixLength;
            public byte SkipAsSource;
            public int DadState;
            public uint ScopeId;
            public long CreationTimeStamp;
        }

        // IP_ADDRESS_PREFIX: a SOCKADDR_INET (28, align 8) plus a UINT8 length.
        // The length byte sits in the SOCKADDR's 8-byte padding tail, so the whole
        // structure is 32 bytes and the following member is 8-aligned at +32.
        [StructLayout(LayoutKind.Sequential)]
        private struct IpAddressPrefix
        {
            public SockAddrInet Prefix;
            public byte PrefixLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibIpforwardRow2
        {
            public ulong InterfaceLuid;
            public uint InterfaceIndex;
            public IpAddressPrefix DestinationPrefix; // 32 bytes; NextHop lands at +48
            public SockAddrInet NextHop;
            public byte SitePrefixLength;
            public uint ValidLifetime;
            public uint PreferredLifetime;
            public uint Metric;
            public int Protocol;
            public byte Loopback;
            public byte AutoconfigureAddress;
            public byte Publish;
            public byte Immortal;
            public uint Age;
            public int Origin;
        }

        private const ushort AF_INET = 2;
        private const int NO_ERROR = 0;

        [DllImport("iphlpapi.dll")]
        private static extern void InitializeUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

        [DllImport("iphlpapi.dll")]
        private static extern int CreateUnicastIpAddressEntry(ref MibUnicastIpAddressRow row);

        [DllImport("iphlpapi.dll")]
        private static extern void InitializeIpForwardEntry(ref MibIpforwardRow2 row);

        [DllImport("iphlpapi.dll")]
        private static extern int CreateIpForwardEntry2(ref MibIpforwardRow2 row);

        // LUID -> interface index, needed for the netsh fallback.
        [DllImport("iphlpapi.dll")]
        private static extern int ConvertInterfaceLuidToIndex(ref ulong luid, out uint index);

        private static uint AddrToUint(IPAddress addr)
        {
            byte[] b = addr.GetAddressBytes(); // already network order for IPv4
            return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
        }

        private static bool TrySetAddressApi(ulong luid, IPAddress addr, int prefixLen)
        {
            var row = new MibUnicastIpAddressRow();
            InitializeUnicastIpAddressEntry(ref row);
            row.InterfaceLuid = luid;
            row.Address.si_family = AF_INET;
            row.Address.sin_addr = AddrToUint(addr);
            row.OnLinkPrefixLength = (byte)prefixLen;
            // DadState preferred (5) => usable immediately on Win10+ (optimistic DAD).
            row.DadState = 5;

            int err = CreateUnicastIpAddressEntry(ref row);
            if (err == NO_ERROR) return true;
            L("CreateUnicastIpAddressEntry returned " + err);
            return false;
        }

        private static bool TryAddRouteApi(ulong luid, IPAddress dest, int prefixLen)
        {
            var row = new MibIpforwardRow2();
            InitializeIpForwardEntry(ref row);
            row.InterfaceLuid = luid;
            row.DestinationPrefix.Prefix.si_family = AF_INET;
            row.DestinationPrefix.Prefix.sin_addr = AddrToUint(dest);
            row.DestinationPrefix.PrefixLength = (byte)prefixLen;
            row.NextHop.si_family = AF_INET;
            row.NextHop.sin_addr = 0;   // on-link (0.0.0.0) for a point-to-point tunnel
            row.Metric = 0;

            int err = CreateIpForwardEntry2(ref row);
            if (err == NO_ERROR || err == 5010 /* ERROR_OBJECT_ALREADY_EXISTS */) return true;
            L("CreateIpForwardEntry2 for route " + dest + "/" + prefixLen + " returned " + err + "; trying netsh.");
            return false;
        }

        // ---------------- netsh fallback ----------------

        private static uint LuidToIndex(ulong luid)
        {
            uint idx;
            int err = ConvertInterfaceLuidToIndex(ref luid, out idx);
            if (err != NO_ERROR) throw new Exception("ConvertInterfaceLuidToIndex failed: " + err);
            return idx;
        }

        /// <summary>
        /// Set the tunnel interface's DNS servers (and optional search domains).
        /// Mirrors the official client: IPv4 and IPv6 servers are applied to the
        /// adapter; the firewall-scoping that forces queries to only these servers
        /// is handled by the kill-switch when it's active.
        /// </summary>
        public static void SetDns(ulong luid, System.Collections.Generic.List<string> servers,
                                  System.Collections.Generic.List<string> searchDomains)
        {
            if (servers == null || servers.Count == 0) return;
            uint idx = LuidToIndex(luid);

            bool firstV4 = true, firstV6 = true;
            foreach (string s in servers)
            {
                System.Net.IPAddress ip;
                if (!System.Net.IPAddress.TryParse(s, out ip)) continue;
                bool v6 = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                string proto = v6 ? "ipv6" : "ipv4";
                try
                {
                    if (v6 ? firstV6 : firstV4)
                    {
                        // primary server: replace the list
                        RunNetsh("interface " + proto + " set dnsservers name=" + idx +
                                 " static " + s + " primary validate=no");
                        if (v6) firstV6 = false; else firstV4 = false;
                    }
                    else
                    {
                        RunNetsh("interface " + proto + " add dnsservers name=" + idx +
                                 " address=" + s + " index=2 validate=no");
                    }
                }
                catch (Exception ex) { L("Could not set DNS " + s + ": " + ex.Message); }
            }

            if (searchDomains != null && searchDomains.Count > 0)
                L("DNS search domains specified (" + string.Join(", ", searchDomains.ToArray()) +
                  "); per-interface search suffix isn't applied (Windows uses a global list).");
            L("DNS servers set on the tunnel interface.");
        }

        /// <summary>
        /// Set the tunnel interface MTU. If desiredMtu is 0, derive it from the
        /// system's default-route interface (its MTU minus 80, the WireGuard
        /// overhead), exactly like the official client; clamps to a sane range.
        /// </summary>
        public static void SetMtu(ulong luid, int desiredMtu)
        {
            int mtu = desiredMtu;
            if (mtu <= 0)
            {
                int baseMtu = DetectDefaultRouteMtu();
                mtu = baseMtu > 0 ? baseMtu - 80 : 1420; // WireGuard default fallback
            }
            if (mtu < 1280) mtu = 1280;     // IPv6 minimum
            if (mtu > 1500) mtu = 1500;

            uint idx = LuidToIndex(luid);
            try
            {
                RunNetsh("interface ipv4 set subinterface " + idx + " mtu=" + mtu + " store=active");
                RunNetsh("interface ipv6 set subinterface " + idx + " mtu=" + mtu + " store=active");
                L("Interface MTU set to " + mtu + (desiredMtu <= 0 ? " (auto)." : "."));
            }
            catch (Exception ex) { L("Could not set MTU: " + ex.Message); }
        }

        // Best-effort: find the MTU of the physical interface that owns the default
        // route, so we can size the tunnel MTU below it.
        private static int DetectDefaultRouteMtu()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Description != null &&
                        ni.Description.IndexOf("WgSharp", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    bool hasDefault = false;
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        if (gw.Address != null) { hasDefault = true; break; }
                    if (!hasDefault) continue;
                    try
                    {
                        IPv4InterfaceProperties v4 = props.GetIPv4Properties();
                        if (v4 != null && v4.Mtu > 0) return v4.Mtu;
                    }
                    catch { }
                }
            }
            catch { }
            return 1500; // typical Ethernet default
        }

        private static void SetAddressNetsh(ulong luid, IPAddress addr, int prefixLen)
        {
            uint idx = LuidToIndex(luid);
            bool v6 = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            if (v6)
            {
                // netsh interface ipv6 set address <idx> <addr>/<prefix>
                RunNetsh("interface ipv6 set address " + idx + " " + addr + "/" + prefixLen);
            }
            else
            {
                string mask = PrefixToMask(prefixLen).ToString();
                RunNetsh("interface ipv4 set address name=" + idx +
                         " static " + addr + " " + mask);
            }
            L("Address " + addr + "/" + prefixLen + " set via netsh (if " + idx + ").");
        }

        private static void AddRouteNetsh(ulong luid, IPAddress dest, int prefixLen)
        {
            uint idx = LuidToIndex(luid);
            bool v6 = dest.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
            string proto = v6 ? "ipv6" : "ipv4";
            // Explicit low metric: without it, netsh assigns an automatic metric,
            // and a virtual Wintun adapter often gets a WORSE automatic metric than
            // the physical NIC. That means a 0.0.0.0/0 tunnel route loses the race
            // against the existing physical default route, so real traffic keeps
            // leaving via the physical adapter (where the kill-switch then silently
            // blocks it) instead of through the tunnel. metric=0 makes the tunnel
            // route win.
            RunNetsh("interface " + proto + " add route " + dest + "/" + prefixLen +
                     " " + idx + " store=active metric=0");
            L("Route " + dest + "/" + prefixLen + " added via netsh (if " + idx + ", metric=0).");
        }

        private static IPAddress PrefixToMask(int prefixLen)
        {
            uint mask = prefixLen == 0 ? 0 : 0xFFFFFFFFu << (32 - prefixLen);
            byte[] b = new byte[4];
            b[0] = (byte)(mask >> 24);
            b[1] = (byte)(mask >> 16);
            b[2] = (byte)(mask >> 8);
            b[3] = (byte)mask;
            return new IPAddress(b);
        }

        // Force a low, fixed interface metric on the tunnel adapter. Windows often
        // computes the EFFECTIVE route metric as (interface metric + route metric);
        // a Wintun virtual adapter is frequently assigned a high automatic interface
        // metric, which can still lose to the physical NIC even after the route
        // itself is added with metric=0. Setting the interface metric explicitly
        // (and turning off "automatic metric") removes that ambiguity.
        public static void SetInterfaceMetric(ulong luid, int metric)
        {
            uint idx;
            try { idx = LuidToIndex(luid); }
            catch (Exception ex) { L("Could not resolve interface index for metric: " + ex.Message); return; }

            try
            {
                RunNetsh("interface ipv4 set interface " + idx + " metric=" + metric);
                L("Tunnel interface IPv4 metric set to " + metric + " (if " + idx + ").");
            }
            catch (Exception ex) { L("Setting IPv4 interface metric failed: " + ex.Message); }

            try
            {
                RunNetsh("interface ipv6 set interface " + idx + " metric=" + metric);
                L("Tunnel interface IPv6 metric set to " + metric + " (if " + idx + ").");
            }
            catch (Exception ex) { L("Setting IPv6 interface metric failed: " + ex.Message); }
        }

        // Dump the active default-route entries so we can see, in the log, which
        // route Windows is ACTUALLY using and at what effective metric — instead of
        // inferring it from whether traffic flows. Best-effort; failures are logged
        // and swallowed so this never blocks the connect.
        public static void DumpRouteDiagnostics(ulong luid)
        {
            try
            {
                string outp = RunNetshCapture("interface ipv4 show route store=active");
                L("---- active IPv4 routes (filtered to default + tunnel if) ----");
                foreach (string line in outp.Split('\n'))
                {
                    string t = line.Trim();
                    if (t.Length == 0) continue;
                    if (t.IndexOf("0.0.0.0/0", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("0.0.0.0/1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("128.0.0.0/1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("Idx", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        t.IndexOf("---", StringComparison.OrdinalIgnoreCase) >= 0)
                        L("  " + t);
                }
                L("---------------------------------------------------------------");
            }
            catch (Exception ex) { L("Route diagnostics dump failed: " + ex.Message); }
        }

        private static string RunNetshCapture(string args)
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string errp = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0)
                    throw new Exception("netsh " + args + " -> exit " + p.ExitCode + ": " +
                                        (errp.Length > 0 ? errp : outp));
                return outp;
            }
        }

        private static void RunNetsh(string args)
        {
            var psi = new ProcessStartInfo("netsh", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = Process.Start(psi))
            {
                string outp = p.StandardOutput.ReadToEnd();
                string errp = p.StandardError.ReadToEnd();
                p.WaitForExit(10000);
                if (p.ExitCode != 0)
                    throw new Exception("netsh " + args + " -> exit " + p.ExitCode + ": " +
                                        (errp.Length > 0 ? errp : outp));
            }
        }

        /// <summary>Parse "10.0.0.2/32" into address + prefix. Defaults to /32 if absent.</summary>
        public static bool TryParseCidr(string cidr, out IPAddress addr, out int prefixLen)
        {
            addr = null; prefixLen = 32;
            if (string.IsNullOrEmpty(cidr)) return false;
            string s = cidr.Trim();
            int slash = s.IndexOf('/');
            string ipPart = slash >= 0 ? s.Substring(0, slash) : s;
            if (slash >= 0)
            {
                if (!int.TryParse(s.Substring(slash + 1), NumberStyles.Integer,
                                  CultureInfo.InvariantCulture, out prefixLen)) return false;
            }
            if (!IPAddress.TryParse(ipPart, out addr)) return false;
            // Default prefix depends on family when none was specified.
            if (slash < 0)
                prefixLen = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32;
            return true;
        }
    }
}

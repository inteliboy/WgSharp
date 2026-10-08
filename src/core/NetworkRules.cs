using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WgSharp.Core
{
    /// <summary>
    /// Classifies the machine's current network connection as Trusted,
    /// Untrusted or Offline for the "auto-connect on untrusted networks"
    /// feature. Wi-Fi SSIDs come from the Native Wifi API (wlanapi.dll), which
    /// is locale-independent, unlike scraping "netsh wlan show interfaces".
    /// </summary>
    public static class NetworkRules
    {
        public enum Kind { Offline, Trusted, Untrusted }

        public sealed class Result
        {
            public Kind Kind;
            /// <summary>Stable identity of the current network(s), used to detect "moved to a different network".</summary>
            public string Key = "offline";
            public string Description = "no network";
        }

        // ---- Native Wifi interop (wlanapi.dll) ----
        [DllImport("wlanapi.dll")]
        private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);
        [DllImport("wlanapi.dll")]
        private static extern int WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);
        [DllImport("wlanapi.dll")]
        private static extern int WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);
        [DllImport("wlanapi.dll")]
        private static extern int WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opCode,
            IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr opcodeValueType);
        [DllImport("wlanapi.dll")]
        private static extern void WlanFreeMemory(IntPtr memory);

        private const int WlanIntfOpcodeCurrentConnection = 7;
        private const int WlanInterfaceStateConnected = 1;
        // WLAN_INTERFACE_INFO_LIST: DWORD count, DWORD index, then WLAN_INTERFACE_INFO[]
        //   WLAN_INTERFACE_INFO: GUID (16) + WCHAR[256] description (512) + DWORD state (4) = 532 bytes
        private const int InterfaceInfoSize = 532;
        // WLAN_CONNECTION_ATTRIBUTES: state (4) + mode (4) + WCHAR[256] profile (512), then
        // WLAN_ASSOCIATION_ATTRIBUTES whose first member is DOT11_SSID { ULONG length; UCHAR[32] }
        private const int SsidOffset = 4 + 4 + 512;

        /// <summary>SSIDs of all currently connected Wi-Fi interfaces; empty if none or no WLAN service.</summary>
        public static List<string> ConnectedSsids()
        {
            var result = new List<string>();
            IntPtr client = IntPtr.Zero;
            try
            {
                uint ver;
                if (WlanOpenHandle(2, IntPtr.Zero, out ver, out client) != 0) return result;

                IntPtr list;
                if (WlanEnumInterfaces(client, IntPtr.Zero, out list) != 0) return result;
                try
                {
                    int count = Marshal.ReadInt32(list, 0);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr info = new IntPtr(list.ToInt64() + 8 + (long)i * InterfaceInfoSize);
                        var guid = (Guid)Marshal.PtrToStructure(info, typeof(Guid));
                        int state = Marshal.ReadInt32(info, 16 + 512);
                        if (state != WlanInterfaceStateConnected) continue;

                        uint size; IntPtr data;
                        if (WlanQueryInterface(client, ref guid, WlanIntfOpcodeCurrentConnection,
                                IntPtr.Zero, out size, out data, IntPtr.Zero) != 0) continue;
                        try
                        {
                            int len = Marshal.ReadInt32(data, SsidOffset);
                            if (len <= 0 || len > 32) continue;
                            var bytes = new byte[len];
                            Marshal.Copy(new IntPtr(data.ToInt64() + SsidOffset + 4), bytes, 0, len);
                            result.Add(System.Text.Encoding.UTF8.GetString(bytes));
                        }
                        finally { WlanFreeMemory(data); }
                    }
                }
                finally { WlanFreeMemory(list); }
            }
            catch (Exception) { /* no WLAN API / service: treat as "no Wi-Fi" */ }
            finally
            {
                if (client != IntPtr.Zero) { try { WlanCloseHandle(client, IntPtr.Zero); } catch (Exception) { } }
            }
            return result;
        }

        /// <summary>True if a physical-looking wired Ethernet adapter is up with a default gateway.</summary>
        public static bool WiredConnected()
        {
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.GigabitEthernet &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetT &&
                        nic.NetworkInterfaceType != NetworkInterfaceType.FastEthernetFx) continue;
                    if (IsVirtual(nic)) continue;
                    foreach (GatewayIPAddressInformation gw in nic.GetIPProperties().GatewayAddresses)
                        if (gw.Address != null && !gw.Address.Equals(System.Net.IPAddress.Any) &&
                            !gw.Address.Equals(System.Net.IPAddress.IPv6Any))
                            return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // Tunnel adapters (WgSharp's own, other VPNs) and hypervisor switches
        // also show up as "Ethernet"; they must not count as a wired LAN.
        private static bool IsVirtual(NetworkInterface nic)
        {
            string s = (nic.Description + " " + nic.Name).ToLowerInvariant();
            string[] markers = { "virtual", "vethernet", "hyper-v", "vmware", "vbox", "tap-", "tap ", "wintun",
                                 "wireguard", "wgsharp", "openvpn", "vpn", "tailscale", "zerotier", "loopback" };
            foreach (string m in markers) if (s.IndexOf(m, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Parses the comma/semicolon/newline separated trusted-SSID list.</summary>
        public static List<string> ParseTrusted(string text)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (string p in text.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = p.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        public static Result Classify(string trustedSsids, bool wiredIsTrusted)
        {
            var trusted = ParseTrusted(trustedSsids);
            List<string> ssids = ConnectedSsids();
            bool wired = WiredConnected();

            var r = new Result();
            var keys = new List<string>();
            if (wired) keys.Add("wired");
            foreach (string s in ssids) keys.Add("wifi:" + s);
            if (keys.Count == 0) return r; // Offline

            r.Key = string.Join("|", keys.ToArray());
            r.Description = string.Join(", ", keys.ToArray()).Replace("wifi:", "Wi-Fi ").Replace("wired", "wired Ethernet");

            bool anyTrusted = wired && wiredIsTrusted;
            foreach (string s in ssids)
                foreach (string t in trusted)
                    if (string.Equals(s, t, StringComparison.OrdinalIgnoreCase)) anyTrusted = true;

            r.Kind = anyTrusted ? Kind.Trusted : Kind.Untrusted;
            return r;
        }
    }
}

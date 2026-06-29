using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace WgSharp.Core
{
    /// <summary>
    /// Routes an outbound packet's destination IP to the owning peer by
    /// longest-prefix match over every peer's AllowedIPs, exactly like the kernel
    /// WireGuard cryptokey-routing table. Supports IPv4 and IPv6. Built once when
    /// the tunnel starts; lookups are read-only and thread-safe.
    /// </summary>
    public sealed class AllowedIpRouter
    {
        private sealed class Entry
        {
            public byte[] Network;   // masked network bytes (4 or 16)
            public int Prefix;       // prefix length in bits
            public int PeerIndex;
        }

        private readonly List<Entry> _v4 = new List<Entry>();
        private readonly List<Entry> _v6 = new List<Entry>();

        public void Add(string cidr, int peerIndex)
        {
            IPAddress addr; int prefix;
            if (!TryParseCidr(cidr, out addr, out prefix)) return;
            byte[] raw = addr.GetAddressBytes();
            byte[] net = MaskNetwork(raw, prefix);
            var e = new Entry { Network = net, Prefix = prefix, PeerIndex = peerIndex };
            if (addr.AddressFamily == AddressFamily.InterNetwork) _v4.Add(e);
            else if (addr.AddressFamily == AddressFamily.InterNetworkV6) _v6.Add(e);
        }

        /// <summary>
        /// Return the peer index that should carry a packet to destIp, or -1 if no
        /// AllowedIPs matches. Longest matching prefix wins.
        /// </summary>
        public int Lookup(IPAddress destIp)
        {
            List<Entry> table = destIp.AddressFamily == AddressFamily.InterNetworkV6 ? _v6 : _v4;
            byte[] ip = destIp.GetAddressBytes();
            int best = -1, bestPrefix = -1;
            for (int i = 0; i < table.Count; i++)
            {
                Entry e = table[i];
                if (e.Network.Length != ip.Length) continue;
                if (PrefixMatches(ip, e.Network, e.Prefix) && e.Prefix > bestPrefix)
                {
                    bestPrefix = e.Prefix;
                    best = e.PeerIndex;
                }
            }
            return best;
        }

        /// <summary>Extract the destination IP from a raw IPv4/IPv6 packet, or null.</summary>
        public static IPAddress DestinationOf(byte[] packet, int length)
        {
            if (packet == null || length < 20) return null;
            int version = packet[0] >> 4;
            if (version == 4)
            {
                // IPv4 destination at bytes 16..19
                if (length < 20) return null;
                byte[] d = new byte[4];
                Array.Copy(packet, 16, d, 0, 4);
                return new IPAddress(d);
            }
            if (version == 6)
            {
                // IPv6 destination at bytes 24..39
                if (length < 40) return null;
                byte[] d = new byte[16];
                Array.Copy(packet, 24, d, 0, 16);
                return new IPAddress(d);
            }
            return null;
        }

        private static bool PrefixMatches(byte[] ip, byte[] network, int prefixBits)
        {
            int fullBytes = prefixBits / 8;
            int remBits = prefixBits % 8;
            for (int i = 0; i < fullBytes; i++)
                if (ip[i] != network[i]) return false;
            if (remBits > 0)
            {
                int mask = 0xFF << (8 - remBits) & 0xFF;
                if ((ip[fullBytes] & mask) != (network[fullBytes] & mask)) return false;
            }
            return true;
        }

        private static byte[] MaskNetwork(byte[] raw, int prefixBits)
        {
            byte[] net = (byte[])raw.Clone();
            int fullBytes = prefixBits / 8;
            int remBits = prefixBits % 8;
            for (int i = fullBytes; i < net.Length; i++) net[i] = 0;
            if (remBits > 0 && fullBytes < net.Length)
            {
                int mask = 0xFF << (8 - remBits) & 0xFF;
                net[fullBytes] = (byte)(raw[fullBytes] & mask);
            }
            return net;
        }

        private static bool TryParseCidr(string cidr, out IPAddress addr, out int prefix)
        {
            addr = null; prefix = 0;
            if (string.IsNullOrEmpty(cidr)) return false;
            string s = cidr.Trim();
            int slash = s.IndexOf('/');
            string host = slash >= 0 ? s.Substring(0, slash) : s;
            if (!IPAddress.TryParse(host, out addr)) return false;
            if (slash >= 0)
            {
                if (!int.TryParse(s.Substring(slash + 1), out prefix)) return false;
            }
            else
            {
                prefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            }
            int maxBits = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix < 0 || prefix > maxBits) return false;
            return true;
        }
    }
}

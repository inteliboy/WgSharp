using System;
using System.Collections.Generic;

namespace WgSharp.Core
{
    /// <summary>Parsed wg .conf. v1: single peer, the common fields only.</summary>
    public sealed class Config
    {
        // [Interface]
        public byte[] PrivateKey;        // 32 bytes
        public string Address;           // e.g. 10.0.0.2/32
        public int ListenPort;           // 0 => ephemeral
        public string Dns;               // optional, comma-separated (servers and/or search domains)
        public int Mtu;                  // 0 => auto (derive from default-route interface)

        // [Peer] — flat fields mirror the FIRST peer for the current single-peer
        // data path. Multiple [Peer] sections are also collected into Peers.
        public byte[] PeerPublicKey;     // 32 bytes
        public byte[] PresharedKey;      // 32 bytes or null
        public string Endpoint;          // host:port
        public List<string> AllowedIPs = new List<string>();
        public int PersistentKeepalive;  // seconds, 0 => off

        public List<Peer> Peers = new List<Peer>();

        /// <summary>One [Peer] section.</summary>
        public sealed class Peer
        {
            public byte[] PublicKey;
            public byte[] PresharedKey;
            public string Endpoint;
            public List<string> AllowedIPs = new List<string>();
            public int PersistentKeepalive;
        }

        // App-level option (stored as a comment line we round-trip): block all
        // non-tunnel traffic while connected.
        public bool BlockUntunneled;

        public static Config Parse(string text)
        {
            var cfg = new Config();
            string section = null;
            Peer current = null;

            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    if (section == "peer")
                    {
                        current = new Peer();
                        cfg.Peers.Add(current);
                    }
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim().ToLowerInvariant();
                string val = line.Substring(eq + 1).Trim();

                if (section == "interface")
                {
                    switch (key)
                    {
                        case "privatekey": cfg.PrivateKey = DecodeKey(val); break;
                        case "address": cfg.Address = val; break;
                        case "listenport": cfg.ListenPort = int.Parse(val); break;
                        case "dns": cfg.Dns = val; break;
                        case "mtu":
                            { int m; if (int.TryParse(val, out m)) cfg.Mtu = m; break; }
                    }
                }
                else if (section == "peer" && current != null)
                {
                    switch (key)
                    {
                        case "publickey": current.PublicKey = DecodeKey(val); break;
                        case "presharedkey": current.PresharedKey = DecodeKey(val); break;
                        case "endpoint": current.Endpoint = val; break;
                        case "allowedips":
                            foreach (var ip in val.Split(','))
                                if (ip.Trim().Length > 0) current.AllowedIPs.Add(ip.Trim());
                            break;
                        case "persistentkeepalive": current.PersistentKeepalive = int.Parse(val); break;
                    }
                }
            }

            // Mirror the first peer into the flat fields used by the current
            // single-peer data path.
            if (cfg.Peers.Count > 0)
            {
                Peer p0 = cfg.Peers[0];
                cfg.PeerPublicKey = p0.PublicKey;
                cfg.PresharedKey = p0.PresharedKey;
                cfg.Endpoint = p0.Endpoint;
                cfg.AllowedIPs = p0.AllowedIPs;
                cfg.PersistentKeepalive = p0.PersistentKeepalive;
            }

            // The kill-switch state mirrors the official client: it is ON exactly
            // when AllowedIPs contains the literal default route 0.0.0.0/0. The
            // "off" form splits that into 0.0.0.0/1 + 128.0.0.0/1 (same coverage,
            // but not the literal default, so no blocking firewall rules).
            cfg.BlockUntunneled = false;
            foreach (string ip in cfg.AllowedIPs)
            {
                if (ip.Replace(" ", "") == "0.0.0.0/0") { cfg.BlockUntunneled = true; break; }
            }

            Validate(cfg);
            return cfg;
        }

        private static byte[] DecodeKey(string b64)
        {
            var k = Convert.FromBase64String(b64);
            if (k.Length != 32) throw new FormatException("Key must decode to 32 bytes, got " + k.Length);
            return k;
        }

        private static void Validate(Config c)
        {
            if (c.PrivateKey == null) throw new FormatException("[Interface] PrivateKey is required");
            if (c.PeerPublicKey == null) throw new FormatException("[Peer] PublicKey is required");
            if (string.IsNullOrEmpty(c.Endpoint)) throw new FormatException("[Peer] Endpoint is required");
        }

        /// <summary>
        /// Split the DNS line per the wg-quick convention: entries that parse as IP
        /// addresses are DNS servers; everything else is a search domain.
        /// </summary>
        public List<string> DnsServers()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(Dns)) return list;
            foreach (string part in Dns.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                System.Net.IPAddress ip;
                if (System.Net.IPAddress.TryParse(s, out ip)) list.Add(s);
            }
            return list;
        }

        public List<string> DnsSearchDomains()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(Dns)) return list;
            foreach (string part in Dns.Split(','))
            {
                string s = part.Trim();
                if (s.Length == 0) continue;
                System.Net.IPAddress ip;
                if (!System.Net.IPAddress.TryParse(s, out ip)) list.Add(s);
            }
            return list;
        }
    }
}

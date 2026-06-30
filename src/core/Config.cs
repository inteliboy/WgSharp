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

        // ---- AmneziaWG ("awg") extension fields, all optional ----
        // Standard wg-quick keys that AmneziaWG-aware clients/servers add to
        // [Interface] to disguise the connection from DPI/firewall signature
        // matching, while the underlying Noise_IKpsk2 handshake and transport
        // crypto are completely unmodified — see Tunnel.cs's AWG framing
        // helpers, which translate at the network I/O boundary only. Both
        // sides of a tunnel (this client's config AND the server's own awg
        // config) must use IDENTICAL values for all of these, the same way
        // PrivateKey/PublicKey must match — there is no negotiation.
        //
        //   Jc/Jmin/Jmax  - send Jc random junk UDP packets, each a random
        //                   size in [Jmin, Jmax] bytes, before every
        //                   handshake initiation attempt.
        //   S1/S2         - prepend S1 (resp. S2) random bytes to the
        //                   handshake initiation (resp. response) datagram,
        //                   changing its on-wire size from the WireGuard-
        //                   standard 148/92 bytes.
        //   H1/H2/H3/H4   - replace the standard fixed 4-byte message header
        //                   (type byte + 3 reserved zero bytes) for
        //                   Initiation/Response/CookieReply/Transport
        //                   messages respectively, with these 4-byte values
        //                   instead of the fixed 1/2/3/4.
        // nullable: distinguishes "not present in the file" (no AWG) from an
        // explicit value, including an explicit 0.
        public int? Jc, Jmin, Jmax, S1, S2;
        public uint? H1, H2, H3, H4;

        /// <summary>True if this config carries ANY AmneziaWG extension key.</summary>
        public bool IsAmneziaWg
        {
            get
            {
                return Jc.HasValue || Jmin.HasValue || Jmax.HasValue ||
                       S1.HasValue || S2.HasValue ||
                       H1.HasValue || H2.HasValue || H3.HasValue || H4.HasValue;
            }
        }

        // Effective values with WireGuard-standard defaults filled in, for
        // code that just wants "what do I actually send" without re-checking
        // HasValue everywhere. Only meaningful/used when IsAmneziaWg is true.
        public int EffectiveJc { get { return Jc ?? 0; } }
        public int EffectiveJmin { get { return Jmin ?? 0; } }
        public int EffectiveJmax { get { return Jmax ?? 0; } }
        public int EffectiveS1 { get { return S1 ?? 0; } }
        public int EffectiveS2 { get { return S2 ?? 0; } }
        public uint EffectiveH1 { get { return H1 ?? 1u; } }
        public uint EffectiveH2 { get { return H2 ?? 2u; } }
        public uint EffectiveH3 { get { return H3 ?? 3u; } }
        public uint EffectiveH4 { get { return H4 ?? 4u; } }

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
                        // AmneziaWG extension keys. wg-quick and the official
                        // client just ignore unknown [Interface] keys, so a
                        // non-AWG file is completely unaffected by these
                        // existing; only a file that explicitly sets one of
                        // them gets AWG behavior (see IsAmneziaWg).
                        case "jc":
                            { int v; if (int.TryParse(val, out v)) cfg.Jc = v; break; }
                        case "jmin":
                            { int v; if (int.TryParse(val, out v)) cfg.Jmin = v; break; }
                        case "jmax":
                            { int v; if (int.TryParse(val, out v)) cfg.Jmax = v; break; }
                        case "s1":
                            { int v; if (int.TryParse(val, out v)) cfg.S1 = v; break; }
                        case "s2":
                            { int v; if (int.TryParse(val, out v)) cfg.S2 = v; break; }
                        case "h1":
                            { uint v; if (uint.TryParse(val, out v)) cfg.H1 = v; break; }
                        case "h2":
                            { uint v; if (uint.TryParse(val, out v)) cfg.H2 = v; break; }
                        case "h3":
                            { uint v; if (uint.TryParse(val, out v)) cfg.H3 = v; break; }
                        case "h4":
                            { uint v; if (uint.TryParse(val, out v)) cfg.H4 = v; break; }
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

            if (c.IsAmneziaWg) ValidateAmneziaWg(c);
        }

        /// <summary>
        /// AmneziaWG fields are only meaningful as a complete, mutually
        /// consistent set — a partially-specified set (e.g. only Jc, no
        /// Jmin/Jmax) is almost certainly a typo'd or hand-edited config, not
        /// an intentional minimal one, since real AWG config generators
        /// always emit all of these together. Failing loudly here beats
        /// silently connecting with a guessed default that won't match
        /// whatever the server actually expects (a mismatch on any of these
        /// doesn't error — it just produces a handshake that silently never
        /// completes, which is a much worse failure mode to debug than a
        /// clear message at import time).
        /// </summary>
        private static void ValidateAmneziaWg(Config c)
        {
            if (c.Jc.HasValue || c.Jmin.HasValue || c.Jmax.HasValue)
            {
                if (!c.Jc.HasValue || !c.Jmin.HasValue || !c.Jmax.HasValue)
                    throw new FormatException(
                        "AmneziaWG config has only some of Jc/Jmin/Jmax set; all three are required together.");
                if (c.Jc.Value < 0 || c.Jc.Value > 128)
                    throw new FormatException("AmneziaWG Jc must be between 0 and 128.");
                if (c.Jmin.Value < 0 || c.Jmax.Value < 0 || c.Jmin.Value > c.Jmax.Value)
                    throw new FormatException("AmneziaWG Jmin must be >= 0 and <= Jmax.");
                if (c.Jmax.Value > 8192)
                    throw new FormatException("AmneziaWG Jmax is unreasonably large (>8192 bytes); refusing.");
            }

            if (c.S1.HasValue && (c.S1.Value < 0 || c.S1.Value > 8192))
                throw new FormatException("AmneziaWG S1 must be between 0 and 8192.");
            if (c.S2.HasValue && (c.S2.Value < 0 || c.S2.Value > 8192))
                throw new FormatException("AmneziaWG S2 must be between 0 and 8192.");

            bool anyH = c.H1.HasValue || c.H2.HasValue || c.H3.HasValue || c.H4.HasValue;
            if (anyH)
            {
                if (!(c.H1.HasValue && c.H2.HasValue && c.H3.HasValue && c.H4.HasValue))
                    throw new FormatException(
                        "AmneziaWG config has only some of H1/H2/H3/H4 set; all four are required together.");
                // Must be pairwise distinct: these are how an incoming UDP
                // datagram is told apart as a response/cookie-reply/transport
                // message (see Tunnel.cs's AWG inbound translation). Two equal
                // values make that disambiguation impossible.
                uint h1 = c.H1.Value, h2 = c.H2.Value, h3 = c.H3.Value, h4 = c.H4.Value;
                if (h1 == h2 || h1 == h3 || h1 == h4 || h2 == h3 || h2 == h4 || h3 == h4)
                    throw new FormatException("AmneziaWG H1/H2/H3/H4 must all be different values.");
            }
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

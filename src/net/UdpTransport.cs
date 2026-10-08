using System;
using System.Net;
using System.Net.Sockets;

namespace WgSharp.Net
{
    /// <summary>
    /// UDP transport to a single peer endpoint. WireGuard multiplexes handshake
    /// and transport messages over one socket, demuxed by the first byte.
    ///
    /// PERFORMANCE NOTES:
    ///   - Built on a raw Socket rather than UdpClient. UdpClient.Receive
    ///     allocates a fresh byte[] for every datagram with no way around it;
    ///     Socket.ReceiveFrom fills a caller-supplied buffer, so the inbound
    ///     loop can run a single reusable buffer (see the int-returning
    ///     ReceiveFrom overload). The old allocating overloads remain as
    ///     wrappers for cold-path callers.
    ///   - SO_RCVBUF / SO_SNDBUF are raised well above the OS default. Default
    ///     UDP socket buffers (often 64KB) silently drop datagrams under bulk
    ///     transfer; on a VPN that surfaces as throughput collapse and
    ///     retransmission storms rather than an obvious error. Set generously —
    ///     the memory is only committed as used.
    /// </summary>
    public sealed class UdpTransport : IDisposable
    {
        private readonly Socket _sock;
        private IPEndPoint _peer;
        private readonly string _endpointSpec;   // original "host:port" for re-resolution
        private EndPoint _recvEp = new IPEndPoint(IPAddress.Any, 0); // ref target for ReceiveFrom

        private const int ReceiveBufferBytes = 4 * 1024 * 1024;
        private const int SendBufferBytes = 1 * 1024 * 1024;

        public UdpTransport(string endpoint, int localPort)
        {
            _endpointSpec = endpoint;
            _peer = Resolve(endpoint);
            // Bind to the requested local port (0 = ephemeral). Dual-stack off; IPv4 path.
            _sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _sock.Bind(new IPEndPoint(IPAddress.Any, localPort));
            // Modest receive timeout so the inbound loop can poll _running.
            _sock.ReceiveTimeout = 250;
            // Large kernel buffers to survive bursts (best effort; some systems
            // clamp these, which is fine).
            try { _sock.ReceiveBufferSize = ReceiveBufferBytes; } catch { }
            try { _sock.SendBufferSize = SendBufferBytes; } catch { }
        }

        public IPEndPoint PeerEndpoint { get { return _peer; } }

        /// <summary>
        /// Re-resolve the original endpoint hostname (a DNS-based endpoint may have
        /// moved). Returns true and updates the peer if the resolved address
        /// changed. A literal-IP endpoint never changes, so this is a no-op there.
        /// </summary>
        public bool ReResolve()
        {
            try
            {
                IPEndPoint fresh = Resolve(_endpointSpec);
                if (!fresh.Address.Equals(_peer.Address) || fresh.Port != _peer.Port)
                {
                    _peer = fresh;
                    return true;
                }
            }
            catch { /* transient DNS failure: keep the current endpoint */ }
            return false;
        }

        private static IPEndPoint Resolve(string endpoint)
        {
            int colon = endpoint.LastIndexOf(':');
            if (colon < 0) throw new FormatException("Endpoint must be host:port");
            string host = endpoint.Substring(0, colon);
            int port = int.Parse(endpoint.Substring(colon + 1));

            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr))
            {
                var entries = Dns.GetHostAddresses(host);
                addr = null;
                foreach (var a in entries)
                {
                    if (a.AddressFamily == AddressFamily.InterNetwork) { addr = a; break; }
                }
                if (addr == null) throw new Exception("Could not resolve " + host + " to an IPv4 address");
            }
            return new IPEndPoint(addr, port);
        }

        public void Send(byte[] data, int length)
        {
            _sock.SendTo(data, length, SocketFlags.None, _peer);
        }

        /// <summary>Send to a specific peer endpoint (multi-peer).</summary>
        public void SendTo(byte[] data, int length, IPEndPoint endpoint)
        {
            _sock.SendTo(data, length, SocketFlags.None, endpoint);
        }

        /// <summary>
        /// Receive one datagram into a caller-supplied buffer and report its
        /// source endpoint. Returns the datagram length, or -1 on timeout /
        /// shutdown (so the caller can re-check its running flag). This is the
        /// allocation-free hot-path receive: the same buffer can be reused for
        /// every datagram, because the inbound loop fully consumes each one
        /// before the next call.
        /// </summary>
        public int ReceiveFrom(byte[] buffer, out IPEndPoint from)
        {
            from = null;
            try
            {
                // Socket.ReceiveFrom replaces the ref EndPoint with a NEW
                // IPEndPoint instance built from the packet's source address,
                // so handing 'from' out (and storing it for roaming) is safe —
                // subsequent calls do not mutate previously returned instances.
                int n = _sock.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref _recvEp);
                from = _recvEp as IPEndPoint;
                return n;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut) return -1;
                if (ex.SocketErrorCode == SocketError.Interrupted) return -1;
                if (ex.SocketErrorCode == SocketError.ConnectionReset) return -1; // ICMP port-unreachable echo; ignore
                throw;
            }
            catch (ObjectDisposedException)
            {
                return -1;
            }
        }

        /// <summary>Resolve a peer endpoint spec to an IPEndPoint (static helper).</summary>
        public static IPEndPoint ResolveEndpoint(string spec)
        {
            return Resolve(spec);
        }

        public void Dispose()
        {
            try { _sock.Close(); } catch { }
        }
    }
}

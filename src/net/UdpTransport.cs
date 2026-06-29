using System;
using System.Net;
using System.Net.Sockets;

namespace WgSharp.Net
{
    /// <summary>
    /// UDP transport to a single peer endpoint. WireGuard multiplexes handshake
    /// and transport messages over one socket, demuxed by the first byte.
    /// </summary>
    public sealed class UdpTransport : IDisposable
    {
        private readonly UdpClient _client;
        private IPEndPoint _peer;
        private readonly string _endpointSpec;   // original "host:port" for re-resolution

        public UdpTransport(string endpoint, int localPort)
        {
            _endpointSpec = endpoint;
            _peer = Resolve(endpoint);
            // Bind to the requested local port (0 = ephemeral). Dual-stack off; IPv4 path.
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
            // Modest receive timeout so the inbound loop can poll _running.
            _client.Client.ReceiveTimeout = 250;
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
            _client.Send(data, length, _peer);
        }

        /// <summary>Send to a specific peer endpoint (multi-peer).</summary>
        public void SendTo(byte[] data, int length, IPEndPoint endpoint)
        {
            _client.Send(data, length, endpoint);
        }

        /// <summary>
        /// Receive one datagram and report its source endpoint (for index-based
        /// peer demux and basic roaming). Returns null on timeout.
        /// </summary>
        public byte[] ReceiveFrom(out IPEndPoint from)
        {
            from = null;
            try
            {
                IPEndPoint f = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _client.Receive(ref f);
                from = f;
                return data;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut) return null;
                if (ex.SocketErrorCode == SocketError.Interrupted) return null;
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        /// <summary>Resolve a peer endpoint spec to an IPEndPoint (static helper).</summary>
        public static IPEndPoint ResolveEndpoint(string spec)
        {
            return Resolve(spec);
        }

        /// <summary>
        /// Receive one datagram. Returns null on timeout (so the caller can re-check
        /// its running flag). Updates the peer endpoint on receipt to allow basic
        /// roaming when the source matches an expected address.
        /// </summary>
        public byte[] Receive()
        {
            try
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] data = _client.Receive(ref from);
                return data;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut) return null;
                if (ex.SocketErrorCode == SocketError.Interrupted) return null;
                throw;
            }
            catch (ObjectDisposedException)
            {
                return null; // socket closed during shutdown
            }
        }

        public void Dispose()
        {
            try { _client.Close(); } catch { }
        }
    }
}

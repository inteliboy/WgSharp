using System;
using System.Net;
using WgSharp.Proto;

namespace WgSharp.Core
{
    /// <summary>
    /// Per-peer runtime state. The Tunnel holds one of these per [Peer] section.
    /// The shared UDP socket and Wintun adapter live on the Tunnel; everything that
    /// differs between peers (endpoint, session, handshake, timers, counters) lives
    /// here. Access is guarded by the Tunnel's handshake lock for handshake/session
    /// transitions; counters use interlocked-style updates under the status lock.
    /// </summary>
    public sealed class PeerState
    {
        public readonly int Index;                 // position in the Tunnel's peer list
        public readonly byte[] PublicKey;
        public readonly byte[] PresharedKey;
        public readonly string EndpointSpec;       // original "host:port"
        public readonly int PersistentKeepalive;   // seconds, 0 => off

        public IPEndPoint Endpoint;                // resolved target for this peer
        public IPAddress EndpointAddress;          // == Endpoint.Address, convenience
        public bool EndpointPinned;

        public volatile Session Session;           // null until handshake completes
        public Handshake Pending;                  // in-flight handshake awaiting response
        public byte[] SavedCookie;
        public DateTime SavedCookieAt;

        public DateTime SessionEstablished;
        public DateTime LastHandshakeSent;
        public DateTime LastRecv;
        public DateTime LastSent;
        public DateTime LastHandshakeOk = DateTime.MinValue;
        public int HandshakeAttempts;
        public long LatencyMs = -1;
        public bool GaveUp;        // true once we've stopped retrying (until reset)

        public PeerState(int index, Config.Peer p)
        {
            Index = index;
            PublicKey = p.PublicKey;
            PresharedKey = p.PresharedKey;
            EndpointSpec = p.Endpoint;
            PersistentKeepalive = p.PersistentKeepalive;
        }

        public bool HasEndpoint { get { return !string.IsNullOrEmpty(EndpointSpec); } }
    }
}

using System;

namespace WgSharp.Proto
{
    /// <summary>
    /// WireGuard's protocol timing constants (seconds) and the derived rekey/
    /// keepalive decisions. As an initiator-only client we apply the initiator
    /// rules; responder-only rules are noted but unused.
    /// </summary>
    public static class Timers
    {
        public const double RekeyAfterTime = 120.0;     // initiator opportunistic rekey
        public const double RejectAfterTime = 180.0;    // session unusable past this age
        public const double RekeyTimeout = 5.0;         // handshake retransmit interval
        public const double KeepaliveTimeout = 10.0;    // send keepalive after silence
        public const double RekeyAttemptTime = 90.0;    // give up initiating after this
        public const double RejectAfterTimeTriple = RejectAfterTime * 3.0; // zero keys

        public const ulong RekeyAfterMessages = (1UL << 60);

        /// <summary>Initiator: rekey after sending data once the session is RekeyAfterTime old.</summary>
        public static bool ShouldRekeyOnSend(double sessionAgeSeconds, ulong messagesSent)
        {
            return sessionAgeSeconds >= RekeyAfterTime || messagesSent >= RekeyAfterMessages;
        }

        /// <summary>Session can no longer be used to send/receive.</summary>
        public static bool SessionExpired(double sessionAgeSeconds)
        {
            return sessionAgeSeconds >= RejectAfterTime;
        }

        /// <summary>Small random jitter [0, 333) ms added to handshake retransmit.</summary>
        public static int HandshakeJitterMs()
        {
            return _rng.Next(0, 333);
        }

        private static readonly Random _rng = new Random();
    }
}

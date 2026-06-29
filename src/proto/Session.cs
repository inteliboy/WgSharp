using System;
using WgSharp.Crypto;

namespace WgSharp.Proto
{
    /// <summary>
    /// An established transport session: the two directional keys plus the send
    /// counter and inbound replay window. Encrypts outgoing IP packets into
    /// type-4 transport messages and decrypts incoming ones.
    ///
    /// Transport message layout:
    ///   [0]      type = 4
    ///   [1..3]   reserved (zero)
    ///   [4..7]   receiver index (the peer's local index; LE)
    ///   [8..15]  counter (LE 64-bit)
    ///   [16..]   ChaCha20-Poly1305(key, nonce=counter, plaintext, aad=empty)
    /// </summary>
    public sealed class Session
    {
        private readonly byte[] _sendKey;
        private readonly byte[] _recvKey;
        private readonly uint _remoteIndex; // goes in the receiver field of packets we send
        private readonly uint _localIndex;  // packets addressed to us carry this as receiver
        private readonly ReplayWindow _replay = new ReplayWindow();

        private ulong _sendCounter;
        private readonly object _sendLock = new object();

        public uint LocalIndex { get { return _localIndex; } }
        public uint RemoteIndex { get { return _remoteIndex; } }

        // Reject sessions after too many messages (WireGuard: 2^60), forcing rekey.
        public const ulong RejectAfterMessages = (1UL << 60);

        public Session(TransportKeys keys)
        {
            _sendKey = keys.SendKey;
            _recvKey = keys.RecvKey;
            _localIndex = keys.LocalIndex;
            _remoteIndex = keys.RemoteIndex;
        }

        /// <summary>True if the send counter is exhausted and a rekey is required.</summary>
        public bool SendCounterExhausted { get { return _sendCounter >= RejectAfterMessages; } }

        /// <summary>Wrap a plaintext IP packet into a transport message ready for UDP.</summary>
        public byte[] Encrypt(byte[] plaintext, int offset, int length)
        {
            ulong counter;
            lock (_sendLock) { counter = _sendCounter++; }

            byte[] nonce = ChaCha20Poly1305.NonceFromCounter(counter);
            byte[] pt = plaintext;
            if (offset != 0 || length != plaintext.Length)
            {
                pt = new byte[length];
                Array.Copy(plaintext, offset, pt, 0, length);
            }
            byte[] ct = ChaCha20Poly1305.Encrypt(_sendKey, nonce, pt, EmptyAad);

            byte[] msg = new byte[Messages.TransportHeaderSize + ct.Length];
            msg[0] = Messages.TypeTransport;
            Messages.WriteLE32(msg, Messages.Tr_Receiver, _remoteIndex);
            Messages.WriteLE64(msg, Messages.Tr_Counter, counter);
            Array.Copy(ct, 0, msg, Messages.Tr_Payload, ct.Length);
            return msg;
        }

        /// <summary>
        /// Decrypt an inbound transport message. Returns the plaintext IP packet,
        /// or null if the tag fails or the counter is a replay. A zero-length
        /// result (keepalive) returns an empty array, not null.
        /// </summary>
        public byte[] Decrypt(byte[] msg, int length)
        {
            if (length < Messages.TransportHeaderSize + 16) return null;
            if (msg[0] != Messages.TypeTransport) return null;

            ulong counter = Messages.ReadLE64(msg, Messages.Tr_Counter);
            byte[] nonce = ChaCha20Poly1305.NonceFromCounter(counter);

            int ctLen = length - Messages.Tr_Payload;
            byte[] ct = new byte[ctLen];
            Array.Copy(msg, Messages.Tr_Payload, ct, 0, ctLen);

            byte[] pt = ChaCha20Poly1305.Decrypt(_recvKey, nonce, ct, EmptyAad);
            if (pt == null) return null;                  // bad tag

            // Only update the replay window after authentication succeeds.
            if (!_replay.CheckAndUpdate(counter)) return null; // replay / too old

            return pt;
        }

        private static readonly byte[] EmptyAad = new byte[0];
    }
}

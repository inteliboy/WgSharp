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
    ///
    /// CRYPTO BACKEND:
    /// Tries Windows CNG (BCryptEncrypt / BCryptDecrypt) first; falls back to
    /// the managed ChaCha20Poly1305 implementation if CNG is unavailable (pre-
    /// Windows 10 1709) or fails to load the key. Both paths produce byte-for-
    /// byte identical wire output — the choice is invisible to the peer.
    ///
    /// CNG gives 3-6× higher throughput on bulk data via SIMD-accelerated
    /// ChaCha20-Poly1305 in user space. The managed path is the safety net.
    /// </summary>
    public sealed class Session : IDisposable
    {
        private readonly byte[] _sendKey;   // kept for managed fallback
        private readonly byte[] _recvKey;   // kept for managed fallback
        private readonly uint _remoteIndex;
        private readonly uint _localIndex;
        private readonly ReplayWindow _replay = new ReplayWindow();

        // CNG handles — null if CNG unavailable or key load failed.
        private readonly CngChaCha20Poly1305 _cngSend;
        private readonly CngChaCha20Poly1305 _cngRecv;

        // Whether we're actually using CNG (both handles loaded successfully).
        private readonly bool _useCng;

        private ulong _sendCounter;
        private readonly object _sendLock = new object();

        public uint LocalIndex  { get { return _localIndex; } }
        public uint RemoteIndex { get { return _remoteIndex; } }

        // Reject sessions after too many messages (WireGuard: 2^60), forcing rekey.
        public const ulong RejectAfterMessages = (1UL << 60);

        public Session(TransportKeys keys)
        {
            _sendKey    = keys.SendKey;
            _recvKey    = keys.RecvKey;
            _localIndex  = keys.LocalIndex;
            _remoteIndex = keys.RemoteIndex;

            // Try to load CNG key handles. Both must succeed; if either fails we
            // fall back to the managed path for this whole session (never mix).
            if (CngChaCha20Poly1305.IsAvailable)
            {
                try
                {
                    _cngSend = new CngChaCha20Poly1305(keys.SendKey);
                    _cngRecv = new CngChaCha20Poly1305(keys.RecvKey);
                    _useCng  = true;
                }
                catch
                {
                    // Dispose whichever handle we did create, then fall back.
                    if (_cngSend != null) { _cngSend.Dispose(); _cngSend = null; }
                    if (_cngRecv != null) { _cngRecv.Dispose(); _cngRecv = null; }
                    _useCng = false;
                }
            }
        }

        /// <summary>True if the send counter is exhausted and a rekey is required.</summary>
        public bool SendCounterExhausted { get { return _sendCounter >= RejectAfterMessages; } }

        /// <summary>
        /// True if this session is using CNG-accelerated crypto. Exposed so
        /// the tunnel can log which path is active on the first packet.
        /// </summary>
        public bool UsingCng { get { return _useCng; } }

        // Reusable nonce buffers. _sendNonce is guarded by _sendLock (Encrypt is
        // called from both the outbound loop and the keepalive timer). _recvNonce
        // relies on Decrypt/DecryptInto being called only from the single inbound
        // loop thread, which is the case today; if a second inbound worker is
        // ever added, give each its own Session-facing scratch.
        private readonly byte[] _sendNonce = new byte[12];
        private readonly byte[] _recvNonce = new byte[12];

        /// <summary>
        /// Wrap a plaintext IP packet into a transport message ready for UDP.
        /// The payload is encrypted directly into the returned message buffer
        /// (header written first, ciphertext+tag at Tr_Payload) — no
        /// intermediate ciphertext array or copy. The whole operation runs
        /// under _sendLock so the counter, nonce scratch, and CNG handle are
        /// used consistently; keepalives (the only other caller) are rare
        /// enough that the serialization is free in practice, and the CNG
        /// backend serializes on its own key-handle lock anyway.
        /// </summary>
        public byte[] Encrypt(byte[] plaintext, int offset, int length)
        {
            byte[] msg = new byte[Messages.TransportHeaderSize + length + 16];
            EncryptInto(plaintext, offset, length, msg);
            return msg;
        }

        /// <summary>
        /// Same as Encrypt but writes the transport message into a caller-owned
        /// buffer (must hold at least TransportHeaderSize + length + 16 bytes)
        /// and returns the message length, so the outbound loop can reuse one
        /// buffer instead of allocating per packet.
        /// </summary>
        public int EncryptInto(byte[] plaintext, int offset, int length, byte[] msg)
        {
            lock (_sendLock)
            {
                // Hard stop at the message ceiling. Time-based rekey and the
                // tunnel maintenance loop normally retire a session long before
                // this, but never emit a packet at or past RejectAfterMessages:
                // continuing would eventually roll the 64-bit counter and reuse
                // a (key, nonce) pair, which is catastrophic for ChaCha20-Poly1305.
                // Fail closed instead — the caller drops the packet and the
                // session is rekeyed.
                if (_sendCounter >= RejectAfterMessages)
                    throw new InvalidOperationException("Send counter exhausted; session must be rekeyed.");
                ulong counter = _sendCounter++;

                ChaCha20Poly1305.NonceFromCounterInto(counter, _sendNonce);

                msg[0] = Messages.TypeTransport;
                msg[1] = 0; msg[2] = 0; msg[3] = 0; // reserved; buffer may be reused
                Messages.WriteLE32(msg, Messages.Tr_Receiver, _remoteIndex);
                Messages.WriteLE64(msg, Messages.Tr_Counter, counter);

                if (_useCng)
                    _cngSend.EncryptInto(plaintext, offset, length, _sendNonce, msg, Messages.Tr_Payload);
                else
                    ChaCha20Poly1305.EncryptInto(_sendKey, _sendNonce, plaintext, offset, length,
                                                 EmptyAad, msg, Messages.Tr_Payload);
                return Messages.TransportHeaderSize + length + 16;
            }
        }

        /// <summary>
        /// Decrypt an inbound transport message into a caller-supplied buffer
        /// (which must hold at least length - TransportHeaderSize - 16 bytes).
        /// Returns true with ptLen set on success — ptLen == 0 is a keepalive.
        /// Returns false if the message is malformed, the tag fails, or the
        /// counter is a replay. Nothing is allocated on this path, so the
        /// inbound loop can run a single reusable plaintext buffer.
        /// Single-threaded by contract: see _recvNonce.
        /// </summary>
        public bool DecryptInto(byte[] msg, int length, byte[] output, out int ptLen)
        {
            ptLen = 0;
            if (length < Messages.TransportHeaderSize + 16) return false;
            if (msg[0] != Messages.TypeTransport) return false;

            ulong counter = Messages.ReadLE64(msg, Messages.Tr_Counter);
            ChaCha20Poly1305.NonceFromCounterInto(counter, _recvNonce);

            int ctLen = length - Messages.Tr_Payload; // ciphertext + tag

            bool ok;
            if (_useCng)
                ok = _cngRecv.DecryptInto(msg, Messages.Tr_Payload, ctLen, _recvNonce, output, 0);
            else
                ok = ChaCha20Poly1305.DecryptInto(_recvKey, _recvNonce, msg, Messages.Tr_Payload, ctLen,
                                                  EmptyAad, output, 0);
            if (!ok) return false; // bad tag

            // Only update the replay window after authentication succeeds.
            if (!_replay.CheckAndUpdate(counter)) return false; // replay / too old

            ptLen = ctLen - 16;
            return true;
        }

        /// <summary>
        /// Decrypt an inbound transport message. Returns the plaintext IP packet,
        /// or null if the tag fails or the counter is a replay. A zero-length
        /// result (keepalive) returns an empty array, not null. Allocating
        /// wrapper over DecryptInto, kept for callers that want an owned array.
        /// </summary>
        public byte[] Decrypt(byte[] msg, int length)
        {
            if (length < Messages.TransportHeaderSize + 16) return null;
            byte[] pt = new byte[length - Messages.Tr_Payload - 16];
            int n;
            if (!DecryptInto(msg, length, pt, out n)) return null;
            return pt;
        }

        public void Dispose()
        {
            if (_cngSend != null) _cngSend.Dispose();
            if (_cngRecv != null) _cngRecv.Dispose();
            // Wipe the managed key copies. Even when CNG is used, the raw bytes
            // were passed to BCryptGenerateSymmetricKey and still sit in the
            // managed heap until GC collects them — zero them explicitly now.
            if (_sendKey != null) Array.Clear(_sendKey, 0, _sendKey.Length);
            if (_recvKey != null) Array.Clear(_recvKey, 0, _recvKey.Length);
        }

        private static readonly byte[] EmptyAad = new byte[0];
    }
}

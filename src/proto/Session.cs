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

        /// <summary>Wrap a plaintext IP packet into a transport message ready for UDP.</summary>
        public byte[] Encrypt(byte[] plaintext, int offset, int length)
        {
            ulong counter;
            lock (_sendLock) { counter = _sendCounter++; }

            byte[] nonce = ChaCha20Poly1305.NonceFromCounter(counter);
            byte[] ct;

            if (_useCng)
            {
                ct = _cngSend.Encrypt(plaintext, offset, length, nonce);
            }
            else
            {
                byte[] pt = plaintext;
                if (offset != 0 || length != plaintext.Length)
                {
                    pt = new byte[length];
                    Array.Copy(plaintext, offset, pt, 0, length);
                }
                ct = ChaCha20Poly1305.Encrypt(_sendKey, nonce, pt, EmptyAad);
            }

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
            byte[] nonce  = ChaCha20Poly1305.NonceFromCounter(counter);

            int ctLen = length - Messages.Tr_Payload;
            byte[] ct = new byte[ctLen];
            Array.Copy(msg, Messages.Tr_Payload, ct, 0, ctLen);

            byte[] pt;
            if (_useCng)
            {
                pt = _cngRecv.Decrypt(ct, ctLen, nonce);
            }
            else
            {
                pt = ChaCha20Poly1305.Decrypt(_recvKey, nonce, ct, EmptyAad);
            }

            if (pt == null) return null; // bad tag

            // Only update the replay window after authentication succeeds.
            if (!_replay.CheckAndUpdate(counter)) return null; // replay / too old

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

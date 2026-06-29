using System;
using System.Security.Cryptography;
using WgSharp.Crypto;

namespace WgSharp.Proto
{
    /// <summary>Result of a completed handshake: the two directional transport keys.</summary>
    public sealed class TransportKeys
    {
        public byte[] SendKey;      // initiator -> responder
        public byte[] RecvKey;      // responder -> initiator
        public uint LocalIndex;     // our sender index
        public uint RemoteIndex;    // peer's sender index (their "receiver" for us)
    }

    /// <summary>
    /// Noise_IKpsk2 handshake, initiator role only (a client dialing a server).
    /// Drives the chaining key (Ck) and transcript hash (H) per the WireGuard
    /// spec, producing transport keys on a valid response.
    ///
    /// Construction labels are hashed once into the initial Ck/H. The PSK is
    /// optional; an all-zero PSK is the no-PSK case and is mixed identically.
    /// </summary>
    public sealed class Handshake
    {
        private readonly byte[] _staticPrivate;   // our static private (s)
        private readonly byte[] _staticPublic;    // our static public (S)
        private readonly byte[] _peerStaticPublic;// responder static public (Sr), known a priori
        private readonly byte[] _presharedKey;    // 32 bytes; zeros if unused

        private byte[] _ephemeralPrivate;         // e (per handshake)
        private byte[] _ephemeralPublic;          // E

        private byte[] _ck = new byte[32];        // chaining key
        private byte[] _h = new byte[32];          // hash
        private uint _localIndex;

        // Cookie state (DoS mitigation). A cookie reply gives us a cookie that we
        // use to compute mac2 on the next initiation; cookies expire after 120s.
        private byte[] _cookie;
        private DateTime _cookieReceived;
        private byte[] _lastSentMac1;              // mac1 of our last init, for cookie AAD

        // Precomputed initial Ck and H (depend only on construction/identifier/peer key).
        private static byte[] _initialChainingKey;
        private static byte[] _initialHashBase;

        public uint LocalIndex { get { return _localIndex; } }
        public byte[] EphemeralPublic { get { return _ephemeralPublic; } }

        // Carry a cookie forward to a fresh handshake (cookies outlive a single
        // initiation attempt; the next init uses it for mac2).
        public void SeedCookie(byte[] cookie, DateTime received)
        {
            _cookie = cookie;
            _cookieReceived = received;
        }
        public byte[] CurrentCookie { get { return _cookie; } }
        public DateTime CookieReceivedAt { get { return _cookieReceived; } }

        static Handshake()
        {
            // Ck0 = HASH(Construction)
            _initialChainingKey = Blake2s.Hash(Messages.Utf8(Messages.Construction));
            // H0base = HASH(Ck0 || Identifier)
            _initialHashBase = Blake2s.Hash(Concat(_initialChainingKey, Messages.Utf8(Messages.Identifier)));
        }

        public Handshake(byte[] staticPrivate, byte[] peerStaticPublic, byte[] presharedKey)
        {
            _staticPrivate = staticPrivate;
            _staticPublic = Curve25519.ScalarMultBase(staticPrivate);
            _peerStaticPublic = peerStaticPublic;
            _presharedKey = presharedKey ?? new byte[32];
        }

        // ---- hash chain helpers ----
        private void MixHash(byte[] data)
        {
            var b = new Blake2s(32);
            b.Update(_h, 0, 32);
            b.Update(data, 0, data.Length);
            _h = b.Finish();
        }

        private void MixKey(byte[] input)
        {
            _ck = Kdf.Derive1(_ck, input);
        }

        private byte[] MixKeyAndHash(byte[] input)
        {
            byte[] t1, t2, t3;
            Kdf.Derive3(_ck, input, out t1, out t2, out t3);
            _ck = t1;
            MixHash(t2);     // t2 is mixed into the transcript hash
            return t3;       // t3 is the temporary key for the next AEAD
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length + b.Length];
            Array.Copy(a, r, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }

        /// <summary>
        /// Build the 148-byte handshake initiation. Mutates internal state so the
        /// subsequent response can be consumed against the same Ck/H.
        /// </summary>
        public byte[] CreateInitiation()
        {
            Array.Copy(_initialChainingKey, _ck, 32);
            Array.Copy(_initialHashBase, _h, 32);

            // H = HASH(H || Sr_pub)
            MixHash(_peerStaticPublic);

            // ephemeral keypair
            _ephemeralPrivate = RandomScalar();
            _ephemeralPublic = Curve25519.ScalarMultBase(_ephemeralPrivate);

            var msg = new byte[Messages.InitiationSize];
            msg[0] = Messages.TypeInitiation;
            _localIndex = RandomIndex();
            Messages.WriteLE32(msg, Messages.Init_Sender, _localIndex);

            // Ck = KDF1(Ck, E); msg.ephemeral = E; H = HASH(H || E)
            MixKey(_ephemeralPublic);
            Array.Copy(_ephemeralPublic, 0, msg, Messages.Init_Ephemeral, 32);
            MixHash(_ephemeralPublic);

            // (Ck, k) = KDF2(Ck, DH(e, Sr)); encrypt static pub under k; mix into H
            byte[] es = Curve25519.ScalarMult(_ephemeralPrivate, _peerStaticPublic);
            byte[] k1;
            byte[] ckTmp1;
            Kdf.Derive2(_ck, es, out ckTmp1, out k1);
            _ck = ckTmp1;
            byte[] encStatic = ChaCha20Poly1305.Encrypt(k1, ZeroNonce, _staticPublic, _h);
            Array.Copy(encStatic, 0, msg, Messages.Init_EncStatic, encStatic.Length);
            MixHash(encStatic);

            // (Ck, k) = KDF2(Ck, DH(s, Sr)); encrypt timestamp under k; mix into H
            byte[] ss = Curve25519.ScalarMult(_staticPrivate, _peerStaticPublic);
            byte[] k2;
            byte[] ckTmp2;
            Kdf.Derive2(_ck, ss, out ckTmp2, out k2);
            _ck = ckTmp2;
            byte[] timestamp = Tai64N.Now();
            byte[] encTs = ChaCha20Poly1305.Encrypt(k2, ZeroNonce, timestamp, _h);
            Array.Copy(encTs, 0, msg, Messages.Init_EncTimestamp, encTs.Length);
            MixHash(encTs);

            // mac1 = MAC(HASH(LabelMac1 || Sr_pub), msg[0..mac1])
            AppendMac1(msg, Messages.Init_Mac1);

            // mac2 = MAC(cookie, msg[0..mac2]) when we hold a valid (unexpired)
            // cookie from a prior cookie-reply; otherwise it stays zero. Servers
            // under load reply with a cookie and require mac2 on the retry.
            AppendMac2(msg, Messages.Init_Mac2);

            // Remember the mac1 we just sent; a cookie reply is authenticated
            // against it, so we need it to decrypt an incoming cookie.
            _lastSentMac1 = Slice(msg, Messages.Init_Mac1, 16);

            return msg;
        }

        // mac2 = BLAKE2s-keyed-128(cookie, msg[0..mac2]) if we have a fresh cookie.
        private void AppendMac2(byte[] msg, int mac2Offset)
        {
            if (_cookie == null) return; // leave zero
            if ((DateTime.UtcNow - _cookieReceived).TotalSeconds > 120) return; // expired
            byte[] mac = Blake2s.HashKeyed(_cookie, Slice(msg, 0, mac2Offset), 16);
            Array.Copy(mac, 0, msg, mac2Offset, 16);
        }

        /// <summary>
        /// Consume a cookie-reply (type 3). Decrypts the cookie with
        /// XChaCha20-Poly1305 under HASH(LabelCookie || Sr_pub), AAD = the mac1 of
        /// our last sent initiation. Stores the cookie for the next init's mac2.
        /// Returns true on success.
        /// </summary>
        public bool ConsumeCookieReply(byte[] msg)
        {
            if (msg == null || msg.Length != Messages.CookieReplySize ||
                msg[0] != Messages.TypeCookieReply) return false;
            if (_lastSentMac1 == null) return false;

            uint receiver = Messages.ReadLE32(msg, Messages.Cookie_Receiver);
            if (receiver != _localIndex) return false;

            byte[] nonce = Slice(msg, Messages.Cookie_Nonce, 24);
            byte[] encCookie = Slice(msg, Messages.Cookie_EncCookie, 32); // 16 + tag

            byte[] key = Blake2s.Hash(Concat(Messages.Utf8(Messages.LabelCookie), _peerStaticPublic));
            byte[] cookie = ChaCha20Poly1305.XDecrypt(key, nonce, encCookie, _lastSentMac1);
            if (cookie == null) return false;

            _cookie = cookie;
            _cookieReceived = DateTime.UtcNow;
            return true;
        }

        /// <summary>
        /// Consume the 92-byte response. Returns the transport keys on success,
        /// or null if any AEAD tag fails (bad peer / wrong keys).
        /// </summary>
        public TransportKeys ConsumeResponse(byte[] msg)
        {
            if (msg.Length != Messages.ResponseSize || msg[0] != Messages.TypeResponse) return null;

            uint senderIndex = Messages.ReadLE32(msg, Messages.Resp_Sender);
            uint receiverIndex = Messages.ReadLE32(msg, Messages.Resp_Receiver);
            if (receiverIndex != _localIndex) return null; // not our session

            byte[] peerEphemeral = new byte[32];
            Array.Copy(msg, Messages.Resp_Ephemeral, peerEphemeral, 0, 32);

            // Ck = KDF1(Ck, Er); H = HASH(H || Er)
            MixKey(peerEphemeral);
            MixHash(peerEphemeral);

            // Ck = KDF1(Ck, DH(e, Er))
            byte[] ee = Curve25519.ScalarMult(_ephemeralPrivate, peerEphemeral);
            MixKey(ee);

            // Ck = KDF1(Ck, DH(s, Er))
            byte[] se = Curve25519.ScalarMult(_staticPrivate, peerEphemeral);
            MixKey(se);

            // (Ck, t, k) = KDF3(Ck, PSK); H = HASH(H || t)
            byte[] k = MixKeyAndHash(_presharedKey);

            // decrypt the empty payload to authenticate the transcript
            byte[] encEmpty = new byte[16];
            Array.Copy(msg, Messages.Resp_EncEmpty, encEmpty, 0, 16);
            byte[] plain = ChaCha20Poly1305.Decrypt(k, ZeroNonce, encEmpty, _h);
            if (plain == null) return null; // auth failure
            MixHash(encEmpty);

            // Derive transport keys: (Tsend, Trecv) = KDF2(Ck, empty)
            byte[] sendKey, recvKey;
            Kdf.Derive2(_ck, EmptyArray, out sendKey, out recvKey);

            var keys = new TransportKeys
            {
                SendKey = sendKey,
                RecvKey = recvKey,
                LocalIndex = _localIndex,
                RemoteIndex = senderIndex
            };

            // wipe handshake secrets
            Array.Clear(_ck, 0, 32);
            Array.Clear(_h, 0, 32);
            if (_ephemeralPrivate != null) Array.Clear(_ephemeralPrivate, 0, _ephemeralPrivate.Length);
            return keys;
        }

        // mac1 = BLAKE2s-keyed-128(key = HASH(LabelMac1 || Sr_pub), msg up to mac1 field)
        private void AppendMac1(byte[] msg, int mac1Offset)
        {
            byte[] key = Blake2s.Hash(Concat(Messages.Utf8(Messages.LabelMac1), _peerStaticPublic));
            byte[] mac = Blake2s.HashKeyed(key, Slice(msg, 0, mac1Offset), 16);
            Array.Copy(mac, 0, msg, mac1Offset, 16);
        }

        private static byte[] Slice(byte[] src, int off, int len)
        {
            byte[] r = new byte[len];
            Array.Copy(src, off, r, 0, len);
            return r;
        }

        private static readonly byte[] ZeroNonce = new byte[12];
        private static readonly byte[] EmptyArray = new byte[0];

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        private static byte[] RandomScalar()
        {
            byte[] b = new byte[32];
            Rng.GetBytes(b);
            return b; // clamped inside ScalarMult
        }

        private static uint RandomIndex()
        {
            byte[] b = new byte[4];
            Rng.GetBytes(b);
            return Messages.ReadLE32(b, 0);
        }
    }
}

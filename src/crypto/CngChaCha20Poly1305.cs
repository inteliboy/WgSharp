using System;
using System.Runtime.InteropServices;

namespace WgSharp.Crypto
{
    /// <summary>
    /// Per-session ChaCha20-Poly1305 AEAD backed by Windows CNG (bcrypt.dll),
    /// using a pre-loaded symmetric key handle for maximum throughput.
    ///
    /// DESIGN — why a per-session object rather than static Encrypt/Decrypt calls:
    ///
    /// CNG's BCryptEncrypt is not a stateless function like our managed
    /// ChaCha20Poly1305: it requires an opaque BCRYPT_KEY_HANDLE that holds the
    /// key material and (for AEAD algorithms) a small amount of internal state
    /// reset per encryption call. Creating that handle costs ~5-10µs; destroying
    /// it is also non-trivial. Doing that overhead on every packet would wipe out
    /// the throughput benefit entirely. The right model is one handle per key per
    /// direction, created once at session establishment, reused for every packet,
    /// destroyed when the session ends.
    ///
    /// THREAD SAFETY:
    /// BCryptEncrypt/BCryptDecrypt with the same key handle are NOT safe for
    /// concurrent calls — MSDN is explicit about this. We hold one lock per
    /// direction (matching Session's existing _sendLock model for the counter).
    /// If a parallel outbound path is added later, each worker should get its own
    /// CngChaCha20Poly1305 instance (duplicate the key handle with
    /// BCryptDuplicateKey), not share one.
    ///
    /// AVAILABILITY:
    /// BCRYPT_CHACHA20_POLY1305_ALGORITHM was added in Windows 10 1709 (build
    /// 16299). On older systems BCryptOpenAlgorithmProvider returns
    /// STATUS_NOT_FOUND (0xC0000225). CngChaCha20Poly1305.IsAvailable() lets
    /// callers check once at startup; if false they fall back to the managed
    /// ChaCha20Poly1305 implementation. We probe with a real algorithm open (not
    /// a version check) so the result is always ground-truth.
    ///
    /// WIRE FORMAT COMPATIBILITY:
    /// The CNG output is byte-for-byte identical to our managed implementation
    /// and to the RFC 8439 standard: [ciphertext][16-byte Poly1305 tag]. The
    /// BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO struct separates the tag from the
    /// ciphertext in memory, so we copy both into the output buffer in the right
    /// order to match the format Session.Encrypt/Decrypt expect.
    /// </summary>
    public sealed class CngChaCha20Poly1305 : IDisposable
    {
        // ------------------------------------------------------------------ //
        //  Constants & P/Invokes                                               //
        // ------------------------------------------------------------------ //

        private const string AlgId = "CHACHA20_POLY1305";
        private const int TagSize = 16;
        private const int NonceSize = 12;

        private const int BCRYPT_AUTH_MODE_CHAIN_CALLS_FLAG = 0x00000001;
        // BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO Version
        private const uint BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION = 1;

        private const int STATUS_NOT_FOUND = unchecked((int)0xC0000225);

        // BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO (x64, packed sequential):
        // cbSize         ULONG     0
        // dwInfoVersion  ULONG     4
        // pbNonce        PUCHAR    8
        // cbNonce        ULONG     16
        // pbAuthData     PUCHAR    24  (AAD — null for WireGuard, no AAD)
        // cbAuthData     ULONG     32
        // pbTag          PUCHAR    40  (auth tag buffer)
        // cbTag          ULONG     48
        // pbMacContext   PUCHAR    56  (null, not chaining)
        // cbMacContext   ULONG     64
        // cbAAD          ULONG     68
        // cbData         ULONGLONG 72
        // dwFlags        ULONG     80
        [StructLayout(LayoutKind.Sequential)]
        private struct BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO
        {
            public uint cbSize;
            public uint dwInfoVersion;
            public IntPtr pbNonce;
            public uint cbNonce;
            public IntPtr pbAuthData;
            public uint cbAuthData;
            public IntPtr pbTag;
            public uint cbTag;
            public IntPtr pbMacContext;
            public uint cbMacContext;
            public uint cbAAD;
            public ulong cbData;
            public uint dwFlags;
        }

        [DllImport("bcrypt.dll")]
        private static extern int BCryptOpenAlgorithmProvider(
            out IntPtr phAlgorithm,
            [MarshalAs(UnmanagedType.LPWStr)] string pszAlgId,
            [MarshalAs(UnmanagedType.LPWStr)] string pszImplementation,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptGenerateSymmetricKey(
            IntPtr hAlgorithm, out IntPtr phKey,
            IntPtr pbKeyObject, uint cbKeyObject,
            byte[] pbSecret, uint cbSecret,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyKey(IntPtr hKey);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptEncrypt(
            IntPtr hKey,
            byte[] pbInput, uint cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            IntPtr pbIV, uint cbIV,
            byte[] pbOutput, uint cbOutput,
            out uint pcbResult,
            uint dwFlags);

        [DllImport("bcrypt.dll")]
        private static extern int BCryptDecrypt(
            IntPtr hKey,
            byte[] pbInput, uint cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            IntPtr pbIV, uint cbIV,
            byte[] pbOutput, uint cbOutput,
            out uint pcbResult,
            uint dwFlags);

        // ------------------------------------------------------------------ //
        //  Availability probe (called once at startup)                         //
        // ------------------------------------------------------------------ //

        private static readonly bool _available;
        private static readonly IntPtr _algHandle; // kept open for the process lifetime

        static CngChaCha20Poly1305()
        {
            IntPtr alg;
            int status = BCryptOpenAlgorithmProvider(out alg, AlgId, null, 0);
            if (status == 0 && alg != IntPtr.Zero)
            {
                _algHandle = alg;
                _available = true;
            }
            // On failure (STATUS_NOT_FOUND on pre-1709, or any other error), leave
            // _available = false and _algHandle = IntPtr.Zero. No logging here —
            // this is a static constructor and the Session class logs the outcome.
        }

        /// <summary>
        /// True if Windows CNG supports CHACHA20_POLY1305 on this machine.
        /// Checked once at process start; stable for the process lifetime.
        /// </summary>
        public static bool IsAvailable { get { return _available; } }

        // ------------------------------------------------------------------ //
        //  Per-session state                                                   //
        // ------------------------------------------------------------------ //

        private IntPtr _key;
        private bool _disposed;
        private readonly object _lock = new object();

        // Pre-pinned buffers: allocating a GCHandle per encrypt/decrypt call is
        // measurable overhead at high packet rates. We pin these once at
        // construction and reuse them for the lifetime of the session.
        private readonly byte[] _nonceBuffer = new byte[NonceSize];  // scratch for nonce
        private readonly byte[] _tagBuffer   = new byte[TagSize];    // scratch for auth tag
        private readonly GCHandle _noncePin;
        private readonly GCHandle _tagPin;

        // Reusable output buffer for Encrypt to avoid new byte[] per packet.
        // Resized up (never down) as needed; capacity tracked separately.
        private byte[] _encryptBuf = new byte[1500 + TagSize]; // pre-sized for MTU

        public CngChaCha20Poly1305(byte[] key32)
        {
            if (!_available) throw new NotSupportedException("CNG ChaCha20-Poly1305 is not available on this OS.");
            if (key32 == null || key32.Length != 32) throw new ArgumentException("Key must be 32 bytes.");

            IntPtr keyHandle;
            int status = BCryptGenerateSymmetricKey(_algHandle, out keyHandle,
                IntPtr.Zero, 0, key32, 32, 0);
            if (status != 0 || keyHandle == IntPtr.Zero)
                throw new Exception("BCryptGenerateSymmetricKey failed: 0x" + status.ToString("X8"));
            _key = keyHandle;

            // Pin nonce and tag buffers once — they're used by every encrypt/decrypt
            // call through BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO.pbNonce/.pbTag.
            _noncePin = GCHandle.Alloc(_nonceBuffer, GCHandleType.Pinned);
            _tagPin   = GCHandle.Alloc(_tagBuffer,   GCHandleType.Pinned);
        }

        // ------------------------------------------------------------------ //
        //  Encrypt / Decrypt                                                   //
        // ------------------------------------------------------------------ //

        /// <summary>
        /// Encrypt plaintext[offset..offset+length] with the given 12-byte nonce.
        /// Returns a new byte[] containing ciphertext || 16-byte tag.
        /// Thread-safe via internal lock (CNG key handles are not re-entrant).
        /// </summary>
        public byte[] Encrypt(byte[] plaintext, int offset, int length, byte[] nonce12)
        {
            if (_disposed) throw new ObjectDisposedException("CngChaCha20Poly1305");

            // Ensure the reusable output buffer is large enough.
            int needed = length + TagSize;
            byte[] output;
            lock (_lock)
            {
                if (_encryptBuf.Length < needed) _encryptBuf = new byte[needed + 256]; // headroom
                output = null; // assigned inside the lock below

                // Copy nonce into the pre-pinned scratch buffer.
                Array.Copy(nonce12, _nonceBuffer, NonceSize);
                // Zero the tag scratch so stale bytes never leak out on error.
                Array.Clear(_tagBuffer, 0, TagSize);

                var authInfo = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                authInfo.cbSize        = (uint)Marshal.SizeOf(authInfo);
                authInfo.dwInfoVersion = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION;
                authInfo.pbNonce       = _noncePin.AddrOfPinnedObject();
                authInfo.cbNonce       = NonceSize;
                authInfo.pbTag         = _tagPin.AddrOfPinnedObject();
                authInfo.cbTag         = TagSize;

                byte[] pt = plaintext;
                if (offset != 0 || length != plaintext.Length)
                {
                    pt = new byte[length];
                    Array.Copy(plaintext, offset, pt, 0, length);
                }

                uint cbResult;
                int status = BCryptEncrypt(_key, pt, (uint)length, ref authInfo,
                    IntPtr.Zero, 0, _encryptBuf, (uint)length, out cbResult, 0);
                if (status != 0)
                    throw new Exception("BCryptEncrypt failed: 0x" + status.ToString("X8"));

                // Return a correctly-sized copy: ciphertext || tag.
                output = new byte[needed];
                Array.Copy(_encryptBuf, 0, output, 0, length);
                Array.Copy(_tagBuffer, 0, output, length, TagSize);
            }
            return output;
        }

        /// <summary>
        /// Decrypt msg[Tr_Payload..] which is ciphertext || 16-byte tag.
        /// Returns plaintext, or null if the tag is invalid (wrong key/nonce/tampered).
        /// </summary>
        /// <summary>
        /// Decrypt ciphertextAndTag[0..length] (ciphertext || 16-byte tag).
        /// Returns plaintext, or null if the tag is invalid.
        /// </summary>
        public byte[] Decrypt(byte[] ciphertextAndTag, int length, byte[] nonce12)
        {
            if (_disposed) throw new ObjectDisposedException("CngChaCha20Poly1305");
            if (length < TagSize) return null;

            int ctLen = length - TagSize;
            byte[] ct = new byte[ctLen];
            Array.Copy(ciphertextAndTag, 0, ct, 0, ctLen);
            byte[] pt = new byte[ctLen];

            lock (_lock)
            {
                Array.Copy(nonce12, _nonceBuffer, NonceSize);
                Array.Copy(ciphertextAndTag, ctLen, _tagBuffer, 0, TagSize);

                var authInfo = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                authInfo.cbSize        = (uint)Marshal.SizeOf(authInfo);
                authInfo.dwInfoVersion = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION;
                authInfo.pbNonce       = _noncePin.AddrOfPinnedObject();
                authInfo.cbNonce       = NonceSize;
                authInfo.pbTag         = _tagPin.AddrOfPinnedObject();
                authInfo.cbTag         = TagSize;

                uint cbResult;
                int status = BCryptDecrypt(_key, ct, (uint)ctLen, ref authInfo,
                    IntPtr.Zero, 0, pt, (uint)ctLen, out cbResult, 0);
                if (status != 0) return null;
            }
            return pt;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _disposed = true;
                    if (_key != IntPtr.Zero) { BCryptDestroyKey(_key); _key = IntPtr.Zero; }
                    // Free the pre-pinned nonce/tag handle pairs.
                    if (_noncePin.IsAllocated) _noncePin.Free();
                    if (_tagPin.IsAllocated)   _tagPin.Free();
                }
            }
        }
    }
}

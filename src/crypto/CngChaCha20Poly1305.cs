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

        // IntPtr-based variants of the same entry points, used by the
        // EncryptInto/DecryptInto fast paths: they let us hand bcrypt an
        // address *inside* a caller-owned array (pinned for the duration of
        // the call), so the payload can be encrypted straight into the
        // outgoing transport message and decrypted straight out of the
        // receive buffer — no intermediate plaintext/ciphertext copies.
        [DllImport("bcrypt.dll", EntryPoint = "BCryptEncrypt")]
        private static extern int BCryptEncryptPtr(
            IntPtr hKey,
            IntPtr pbInput, uint cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            IntPtr pbIV, uint cbIV,
            IntPtr pbOutput, uint cbOutput,
            out uint pcbResult,
            uint dwFlags);

        [DllImport("bcrypt.dll", EntryPoint = "BCryptDecrypt")]
        private static extern int BCryptDecryptPtr(
            IntPtr hKey,
            IntPtr pbInput, uint cbInput,
            ref BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO pPaddingInfo,
            IntPtr pbIV, uint cbIV,
            IntPtr pbOutput, uint cbOutput,
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
        /// Encrypt plaintext[offset..offset+length) with the given 12-byte nonce,
        /// writing ciphertext into outBuf[outOff..] and the 16-byte tag
        /// immediately after (length + 16 bytes written in total). The caller's
        /// arrays are pinned for the duration of the bcrypt call so no
        /// intermediate copies are made. Thread-safe via the internal lock
        /// (CNG key handles are not re-entrant).
        /// </summary>
        public void EncryptInto(byte[] plaintext, int offset, int length, byte[] nonce12,
                                byte[] outBuf, int outOff)
        {
            if (_disposed) throw new ObjectDisposedException("CngChaCha20Poly1305");
            if (outBuf.Length - outOff < length + TagSize)
                throw new ArgumentException("Output buffer too small.");

            lock (_lock)
            {
                Array.Copy(nonce12, _nonceBuffer, NonceSize);
                Array.Clear(_tagBuffer, 0, TagSize);

                var authInfo = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                authInfo.cbSize        = (uint)Marshal.SizeOf(authInfo);
                authInfo.dwInfoVersion = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION;
                authInfo.pbNonce       = _noncePin.AddrOfPinnedObject();
                authInfo.cbNonce       = NonceSize;
                authInfo.pbTag         = _tagPin.AddrOfPinnedObject();
                authInfo.cbTag         = TagSize;

                // Pin the caller's arrays only for the duration of the call.
                // A GCHandle pair costs tens of nanoseconds — far cheaper than
                // the per-packet plaintext copy + output copy it replaces.
                GCHandle inPin  = GCHandle.Alloc(plaintext, GCHandleType.Pinned);
                GCHandle outPin = GCHandle.Alloc(outBuf,    GCHandleType.Pinned);
                try
                {
                    IntPtr inPtr  = IntPtr.Add(inPin.AddrOfPinnedObject(),  offset);
                    IntPtr outPtr = IntPtr.Add(outPin.AddrOfPinnedObject(), outOff);
                    uint cbResult;
                    int status = BCryptEncryptPtr(_key, inPtr, (uint)length, ref authInfo,
                        IntPtr.Zero, 0, outPtr, (uint)length, out cbResult, 0);
                    if (status != 0)
                        throw new Exception("BCryptEncrypt failed: 0x" + status.ToString("X8"));
                }
                finally
                {
                    inPin.Free();
                    outPin.Free();
                }

                Array.Copy(_tagBuffer, 0, outBuf, outOff + length, TagSize);
            }
        }

        /// <summary>
        /// Encrypt plaintext[offset..offset+length] with the given 12-byte nonce.
        /// Returns a new byte[] containing ciphertext || 16-byte tag. Allocating
        /// wrapper over EncryptInto.
        /// </summary>
        public byte[] Encrypt(byte[] plaintext, int offset, int length, byte[] nonce12)
        {
            byte[] output = new byte[length + TagSize];
            EncryptInto(plaintext, offset, length, nonce12, output, 0);
            return output;
        }

        /// <summary>
        /// Decrypt input[inOff..inOff+inLen) (ciphertext || 16-byte tag) into
        /// outBuf[outOff..] (inLen - 16 bytes written). Returns false if the tag
        /// is invalid. The caller's arrays are pinned for the duration of the
        /// bcrypt call so no intermediate copies are made.
        /// </summary>
        public bool DecryptInto(byte[] input, int inOff, int inLen, byte[] nonce12,
                                byte[] outBuf, int outOff)
        {
            if (_disposed) throw new ObjectDisposedException("CngChaCha20Poly1305");
            if (inLen < TagSize) return false;
            int ctLen = inLen - TagSize;
            if (outBuf.Length - outOff < ctLen)
                throw new ArgumentException("Output buffer too small.");

            lock (_lock)
            {
                Array.Copy(nonce12, _nonceBuffer, NonceSize);
                Array.Copy(input, inOff + ctLen, _tagBuffer, 0, TagSize);

                var authInfo = new BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO();
                authInfo.cbSize        = (uint)Marshal.SizeOf(authInfo);
                authInfo.dwInfoVersion = BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION;
                authInfo.pbNonce       = _noncePin.AddrOfPinnedObject();
                authInfo.cbNonce      = NonceSize;
                authInfo.pbTag         = _tagPin.AddrOfPinnedObject();
                authInfo.cbTag         = TagSize;

                GCHandle inPin  = GCHandle.Alloc(input,  GCHandleType.Pinned);
                GCHandle outPin = GCHandle.Alloc(outBuf, GCHandleType.Pinned);
                try
                {
                    IntPtr inPtr  = IntPtr.Add(inPin.AddrOfPinnedObject(),  inOff);
                    IntPtr outPtr = IntPtr.Add(outPin.AddrOfPinnedObject(), outOff);
                    uint cbResult;
                    int status = BCryptDecryptPtr(_key, inPtr, (uint)ctLen, ref authInfo,
                        IntPtr.Zero, 0, outPtr, (uint)ctLen, out cbResult, 0);
                    if (status != 0) return false;
                }
                finally
                {
                    inPin.Free();
                    outPin.Free();
                }
            }
            return true;
        }

        /// <summary>
        /// Decrypt ciphertextAndTag[0..length] (ciphertext || 16-byte tag).
        /// Returns plaintext, or null if the tag is invalid. Allocating wrapper
        /// over DecryptInto.
        /// </summary>
        public byte[] Decrypt(byte[] ciphertextAndTag, int length, byte[] nonce12)
        {
            if (length < TagSize) return null;
            byte[] pt = new byte[length - TagSize];
            if (!DecryptInto(ciphertextAndTag, 0, length, nonce12, pt, 0)) return null;
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

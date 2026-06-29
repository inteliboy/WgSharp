using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WgSharp.Core
{
    /// <summary>
    /// Password-based encryption for portable config files. Unlike DPAPI blobs
    /// (which are bound to the machine/user and therefore NOT portable), these
    /// files can be carried to another PC and decrypted with the password.
    ///
    /// Format (all binary, big-endian where multi-byte):
    ///   magic      "WGSP"            (4 bytes)
    ///   version    0x01              (1 byte)
    ///   iterations PBKDF2 count      (4 bytes)
    ///   salt                         (16 bytes)
    ///   iv         AES-CBC IV        (16 bytes)
    ///   ciphertext AES-256-CBC/PKCS7 (variable)
    ///   mac        HMAC-SHA256       (32 bytes, over everything before it)
    ///
    /// Construction: PBKDF2-HMAC-SHA256 derives 64 bytes (32 enc + 32 mac) from
    /// the password and salt. AES-256-CBC encrypts; then HMAC-SHA256 authenticates
    /// the whole header+ciphertext (encrypt-then-MAC). The magic header lets any
    /// WgSharp client recognize the file and prompt for a password on import.
    ///
    /// Note: this protects config files at rest. It is unrelated to the WireGuard
    /// tunnel crypto (which is ChaCha20/Poly1305/BLAKE2s); AES is appropriate here
    /// because it's only file-at-rest encryption.
    /// </summary>
    public static class PortableCrypto
    {
        public static readonly byte[] Magic = Encoding.ASCII.GetBytes("WGSP");
        private const byte Version = 1;
        private const int DefaultIterations = 200000;
        private const int SaltLen = 16;
        private const int IvLen = 16;
        private const int MacLen = 32;

        /// <summary>True if the blob starts with the WgSharp portable magic header.</summary>
        public static bool IsPortableBlob(byte[] blob)
        {
            if (blob == null || blob.Length < Magic.Length + 1) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (blob[i] != Magic[i]) return false;
            return true;
        }

        public static byte[] Encrypt(string configText, string password)
        {
            byte[] salt = new byte[SaltLen];
            byte[] iv = new byte[IvLen];
            using (var rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
                rng.GetBytes(iv);
            }

            byte[] encKey, macKey;
            DeriveKeys(password, salt, DefaultIterations, out encKey, out macKey);

            byte[] plain = Encoding.UTF8.GetBytes(configText);
            byte[] cipher;
            using (var aes = new RijndaelManaged())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var enc = aes.CreateEncryptor())
                    cipher = enc.TransformFinalBlock(plain, 0, plain.Length);
            }
            Array.Clear(plain, 0, plain.Length);

            // header = magic | version | iterations | salt | iv
            byte[] header = new byte[Magic.Length + 1 + 4 + SaltLen + IvLen];
            int o = 0;
            Buffer.BlockCopy(Magic, 0, header, o, Magic.Length); o += Magic.Length;
            header[o++] = Version;
            WriteIntBE(header, o, DefaultIterations); o += 4;
            Buffer.BlockCopy(salt, 0, header, o, SaltLen); o += SaltLen;
            Buffer.BlockCopy(iv, 0, header, o, IvLen); o += IvLen;

            // mac over header+ciphertext
            byte[] toMac = new byte[header.Length + cipher.Length];
            Buffer.BlockCopy(header, 0, toMac, 0, header.Length);
            Buffer.BlockCopy(cipher, 0, toMac, header.Length, cipher.Length);
            byte[] mac;
            using (var h = new HMACSHA256(macKey))
                mac = h.ComputeHash(toMac);

            byte[] result = new byte[toMac.Length + MacLen];
            Buffer.BlockCopy(toMac, 0, result, 0, toMac.Length);
            Buffer.BlockCopy(mac, 0, result, toMac.Length, MacLen);

            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            return result;
        }

        /// <summary>
        /// Decrypt a portable blob. Throws CryptographicException on a wrong
        /// password or tampering (the HMAC check fails before any decryption).
        /// </summary>
        public static string Decrypt(byte[] blob, string password)
        {
            if (!IsPortableBlob(blob)) throw new CryptographicException("Not a WgSharp portable config.");
            int o = Magic.Length;
            byte version = blob[o++];
            if (version != Version) throw new CryptographicException("Unsupported portable config version.");
            int iterations = ReadIntBE(blob, o); o += 4;
            if (iterations < 1 || iterations > 10000000) throw new CryptographicException("Corrupt portable config.");
            byte[] salt = new byte[SaltLen];
            Buffer.BlockCopy(blob, o, salt, 0, SaltLen); o += SaltLen;
            byte[] iv = new byte[IvLen];
            Buffer.BlockCopy(blob, o, iv, 0, IvLen); o += IvLen;

            int cipherLen = blob.Length - o - MacLen;
            if (cipherLen <= 0) throw new CryptographicException("Corrupt portable config.");

            byte[] encKey, macKey;
            DeriveKeys(password, salt, iterations, out encKey, out macKey);

            // verify MAC first (encrypt-then-MAC): over everything before the mac
            byte[] expectMac;
            using (var h = new HMACSHA256(macKey))
                expectMac = h.ComputeHash(blob, 0, blob.Length - MacLen);
            bool ok = true;
            for (int i = 0; i < MacLen; i++)
                if (expectMac[i] != blob[blob.Length - MacLen + i]) ok = false;
            if (!ok)
            {
                Array.Clear(encKey, 0, encKey.Length);
                Array.Clear(macKey, 0, macKey.Length);
                throw new CryptographicException("Incorrect password or corrupt file.");
            }

            byte[] cipher = new byte[cipherLen];
            Buffer.BlockCopy(blob, o, cipher, 0, cipherLen);
            byte[] plain;
            using (var aes = new RijndaelManaged())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = encKey;
                aes.IV = iv;
                using (var dec = aes.CreateDecryptor())
                    plain = dec.TransformFinalBlock(cipher, 0, cipher.Length);
            }
            string text = Encoding.UTF8.GetString(plain);
            Array.Clear(plain, 0, plain.Length);
            Array.Clear(encKey, 0, encKey.Length);
            Array.Clear(macKey, 0, macKey.Length);
            return text;
        }

        private static void DeriveKeys(string password, byte[] salt, int iterations,
                                       out byte[] encKey, out byte[] macKey)
        {
            // PBKDF2-HMAC-SHA256 -> 64 bytes (32 enc + 32 mac).
            using (var kdf = new Rfc2898DeriveBytes(
                       Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256))
            {
                byte[] dk = kdf.GetBytes(64);
                encKey = new byte[32];
                macKey = new byte[32];
                Buffer.BlockCopy(dk, 0, encKey, 0, 32);
                Buffer.BlockCopy(dk, 32, macKey, 0, 32);
                Array.Clear(dk, 0, dk.Length);
            }
        }

        private static void WriteIntBE(byte[] buf, int offset, int value)
        {
            buf[offset] = (byte)(value >> 24);
            buf[offset + 1] = (byte)(value >> 16);
            buf[offset + 2] = (byte)(value >> 8);
            buf[offset + 3] = (byte)value;
        }

        private static int ReadIntBE(byte[] buf, int offset)
        {
            return (buf[offset] << 24) | (buf[offset + 1] << 16) |
                   (buf[offset + 2] << 8) | buf[offset + 3];
        }
    }
}

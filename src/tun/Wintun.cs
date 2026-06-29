using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace WgSharp.Tun
{
    /// <summary>
    /// Thin P/Invoke wrapper over wintun.dll (API 0.14.x).
    /// The DLL must sit next to the executable. Process must be elevated:
    /// creating an adapter touches the driver and the network stack.
    /// </summary>
    internal static class WintunNative
    {
        private const string Dll = "wintun.dll";

        // WINTUN_ADAPTER_HANDLE WintunCreateAdapter(LPCWSTR Name, LPCWSTR TunnelType, const GUID* RequestedGUID)
        [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr WintunCreateAdapter(string name, string tunnelType, IntPtr requestedGuid);

        // WINTUN_ADAPTER_HANDLE WintunOpenAdapter(LPCWSTR Name)
        [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr WintunOpenAdapter(string name);

        [DllImport(Dll, SetLastError = true)]
        public static extern void WintunCloseAdapter(IntPtr adapter);

        // void WintunGetAdapterLUID(WINTUN_ADAPTER_HANDLE Adapter, NET_LUID* Luid)
        [DllImport(Dll, SetLastError = true)]
        public static extern void WintunGetAdapterLUID(IntPtr adapter, out ulong luid);

        // WINTUN_SESSION_HANDLE WintunStartSession(WINTUN_ADAPTER_HANDLE Adapter, DWORD Capacity)
        [DllImport(Dll, SetLastError = true)]
        public static extern IntPtr WintunStartSession(IntPtr adapter, uint capacity);

        [DllImport(Dll, SetLastError = true)]
        public static extern void WintunEndSession(IntPtr session);

        // HANDLE WintunGetReadWaitEvent(WINTUN_SESSION_HANDLE Session)
        [DllImport(Dll, SetLastError = true)]
        public static extern IntPtr WintunGetReadWaitEvent(IntPtr session);

        // BYTE* WintunReceivePacket(WINTUN_SESSION_HANDLE Session, DWORD* PacketSize)
        [DllImport(Dll, SetLastError = true)]
        public static extern IntPtr WintunReceivePacket(IntPtr session, out uint packetSize);

        [DllImport(Dll, SetLastError = true)]
        public static extern void WintunReleaseReceivePacket(IntPtr session, IntPtr packet);

        // BYTE* WintunAllocateSendPacket(WINTUN_SESSION_HANDLE Session, DWORD PacketSize)
        [DllImport(Dll, SetLastError = true)]
        public static extern IntPtr WintunAllocateSendPacket(IntPtr session, uint packetSize);

        [DllImport(Dll, SetLastError = true)]
        public static extern void WintunSendPacket(IntPtr session, IntPtr packet);
    }

    /// <summary>
    /// Managed adapter + session. Owns the unmanaged handles and the ring buffer.
    /// Wintun delivers/accepts raw layer-3 IP packets (no Ethernet framing).
    /// </summary>
    public sealed class WintunAdapter : IDisposable
    {
        // ERROR_NO_MORE_ITEMS — ring is momentarily empty; wait on the read event.
        private const int ERROR_NO_MORE_ITEMS = 259;

        // Build a deterministic RFC 4122 v5 (SHA-1, name-based) GUID from a fixed
        // namespace and the tunnel name. Same name => same GUID, always.
        private static Guid DeterministicGuid(string name)
        {
            // Fixed namespace GUID for WgSharp tunnels (randomly chosen once).
            // Bytes in RFC 4122 big-endian field order.
            byte[] ns = new byte[]
            {
                0x6b, 0xa7, 0xc1, 0x10, 0x9d, 0xad, 0x11, 0xd1,
                0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8
            };
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(name == null ? "" : name);
            byte[] input = new byte[ns.Length + nameBytes.Length];
            System.Buffer.BlockCopy(ns, 0, input, 0, ns.Length);
            System.Buffer.BlockCopy(nameBytes, 0, input, ns.Length, nameBytes.Length);

            byte[] hash;
            using (var sha1 = System.Security.Cryptography.SHA1.Create())
                hash = sha1.ComputeHash(input);

            byte[] g = new byte[16];
            System.Array.Copy(hash, 0, g, 0, 16); // first 16 bytes of the SHA-1
            // set version (5) and RFC 4122 variant
            g[6] = (byte)((g[6] & 0x0F) | 0x50);
            g[8] = (byte)((g[8] & 0x3F) | 0x80);

            // g is in big-endian (network) field order. .NET's Guid(byte[]) expects
            // the first three fields in little-endian, so swap them to construct a
            // Guid whose canonical string matches the RFC 4122 rendering.
            SwapBytes(g, 0, 3);
            SwapBytes(g, 1, 2);
            SwapBytes(g, 4, 5);
            SwapBytes(g, 6, 7);
            return new Guid(g);
        }

        private static void SwapBytes(byte[] b, int i, int j)
        {
            byte t = b[i]; b[i] = b[j]; b[j] = t;
        }



        // Ring capacity: power of two, 128 KiB .. 64 MiB. 4 MiB is WireGuard's own default.
        public const uint DefaultCapacity = 0x400000;

        private IntPtr _adapter;
        private IntPtr _session;
        private AutoResetEvent _readEvent; // wraps the native wait handle (do not close it)

        public WintunAdapter(string name, string tunnelType, uint capacity = DefaultCapacity)
        {
            // Derive a deterministic GUID from the tunnel name so the adapter keeps
            // the same NetCfgInstanceId across reconnects. Windows keys network
            // categorization (public/private firewall profile) off this GUID, so a
            // stable value avoids the "new network" re-prompt / re-categorization
            // every time the tunnel comes up. Mirrors the official client's intent.
            Guid det = DeterministicGuid(name);
            byte[] guidBytes = det.ToByteArray();
            IntPtr guidPtr = Marshal.AllocHGlobal(guidBytes.Length);
            try
            {
                Marshal.Copy(guidBytes, 0, guidPtr, guidBytes.Length);
                _adapter = WintunNative.WintunCreateAdapter(name, tunnelType, guidPtr);
            }
            finally
            {
                Marshal.FreeHGlobal(guidPtr);
            }
            if (_adapter == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "WintunCreateAdapter failed (is wintun.dll present and the process elevated?)");

            _session = WintunNative.WintunStartSession(_adapter, capacity);
            if (_session == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                WintunNative.WintunCloseAdapter(_adapter);
                _adapter = IntPtr.Zero;
                throw new Win32Exception(err, "WintunStartSession failed");
            }

            IntPtr evt = WintunNative.WintunGetReadWaitEvent(_session);
            // Wrap without ownership: Wintun owns this handle and frees it at EndSession.
            _readEvent = new AutoResetEvent(false);
            _readEvent.SafeWaitHandle = new Microsoft.Win32.SafeHandles.SafeWaitHandle(evt, ownsHandle: false);
        }

        public ulong GetLuid()
        {
            ulong luid;
            WintunNative.WintunGetAdapterLUID(_adapter, out luid);
            return luid;
        }

        /// <summary>
        /// Pull one IP packet the OS wants to send out the tunnel.
        /// Returns null if the ring is currently empty (caller should WaitForPacket).
        /// </summary>
        public byte[] ReceivePacket()
        {
            uint size;
            IntPtr ptr = WintunNative.WintunReceivePacket(_session, out size);
            if (ptr == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_NO_MORE_ITEMS) return null;
                throw new Win32Exception(err, "WintunReceivePacket failed");
            }
            try
            {
                var buf = new byte[size];
                Marshal.Copy(ptr, buf, 0, (int)size);
                return buf;
            }
            finally
            {
                WintunNative.WintunReleaseReceivePacket(_session, ptr);
            }
        }

        /// <summary>Block until the ring has data or the timeout elapses.</summary>
        public bool WaitForPacket(int timeoutMs)
        {
            return _readEvent.WaitOne(timeoutMs);
        }

        /// <summary>Inject a decrypted IP packet into the OS as if it arrived on the tunnel.</summary>
        public void SendPacket(byte[] packet, int offset, int length)
        {
            IntPtr ptr = WintunNative.WintunAllocateSendPacket(_session, (uint)length);
            if (ptr == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WintunAllocateSendPacket failed");
            Marshal.Copy(packet, offset, ptr, length);
            WintunNative.WintunSendPacket(_session, ptr);
        }

        public void Dispose()
        {
            if (_readEvent != null) { _readEvent.Dispose(); _readEvent = null; }
            if (_session != IntPtr.Zero) { WintunNative.WintunEndSession(_session); _session = IntPtr.Zero; }
            if (_adapter != IntPtr.Zero) { WintunNative.WintunCloseAdapter(_adapter); _adapter = IntPtr.Zero; }
        }
    }
}

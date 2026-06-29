using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WgSharp.Tun
{
    /// <summary>
    /// A leak-free kill-switch built on the Windows Filtering Platform (WFP) via
    /// P/Invoke against fwpuclnt.dll. Unlike the netsh-based fallback, WFP supports
    /// weighted permit filters in a dedicated sublayer, so a high-weight "permit to
    /// the VPN endpoint / from the tunnel adapter" coexists with a low-weight
    /// "block everything" and the permit wins. This lets the kill-switch engage
    /// immediately (no deferral, no handshake-window leak) the way the official
    /// client does.
    ///
    /// All blocking happens at the ALE_AUTH_CONNECT layers (v4 and v6), which fire
    /// when a socket attempts an outbound connection/first send. Filters are added
    /// inside a single transaction in our own sublayer; Disengage deletes the
    /// sublayer (which removes all our filters). The WFP session is dynamic, so if
    /// the process dies the filters are auto-removed by the system — a useful
    /// safety property (no permanently-blocked network on a crash).
    ///
    /// This is the most marshalling-intensive code in the project. Struct layouts,
    /// field offsets, and union handling must match the Win32 headers exactly; a
    /// mistake typically surfaces as an AccessViolation or a filter that silently
    /// does nothing.
    /// </summary>
    public static class WfpKillSwitch
    {
        public static event Action<string> Log;
        private static void L(string m) { var h = Log; if (h != null) h(WgSharp.Core.Logger.Tag(m, "WFP")); }

        private static IntPtr _engine = IntPtr.Zero;
        private static Guid _subLayerKey;
        private static bool _engaged;

        // ---- well-known WFP GUIDs (from fwpmu.h / fwpmtypes.h) ----
        // Layers at which we install filters. We filter at both the CONNECT
        // (outbound) and RECV_ACCEPT (inbound) ALE layers for v4 and v6 — matching
        // the official client. Missing the RECV_ACCEPT layers was why inbound
        // tunnel/handshake traffic was dropped and nothing flowed.
        private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V4 =
            new Guid("c38d57d1-05a7-4c33-904f-7fbceee60e82");
        private static readonly Guid FWPM_LAYER_ALE_AUTH_CONNECT_V6 =
            new Guid("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
        private static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4 =
            new Guid("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
        private static readonly Guid FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 =
            new Guid("a3b42c97-9f04-4672-b87e-cee9c483257f");
        // Condition keys.
        private static readonly Guid FWPM_CONDITION_IP_REMOTE_ADDRESS =
            new Guid("b235ae9a-1d64-49b8-a44c-5ff3d9095045");
        private static readonly Guid FWPM_CONDITION_IP_LOCAL_ADDRESS =
            new Guid("d9ee00de-c1ef-4617-bfe3-ffd8f5a08957");
        // Local interface LUID (matches traffic on a specific adapter).
        private static readonly Guid FWPM_CONDITION_IP_LOCAL_INTERFACE =
            new Guid("4cd62a49-59c3-4969-b7f3-bda5d32890a4");

        // ---- enums ----
        private const uint FWP_ACTION_FLAG_TERMINATING = 0x00001000;
        private const uint FWP_ACTION_BLOCK = 0x00000001 | FWP_ACTION_FLAG_TERMINATING;
        private const uint FWP_ACTION_PERMIT = 0x00000002 | FWP_ACTION_FLAG_TERMINATING;

        // FWP_MATCH_TYPE
        private const uint FWP_MATCH_EQUAL = 0;

        // FWP_DATA_TYPE (from fwptypes.h): EMPTY=0, UINT8=1, UINT16=2, UINT32=3,
        // UINT64=4, ... V4_ADDR_AND_MASK=0x100, V6_ADDR_AND_MASK=0x101.
        private const uint FWP_UINT8 = 0x00000001;
        private const uint FWP_UINT64 = 0x00000004;
        private const uint FWP_V4_ADDR_MASK = 0x00000100; // FWP_V4_ADDR_AND_MASK*
        private const uint FWP_V6_ADDR_MASK = 0x00000101; // FWP_V6_ADDR_AND_MASK*

        // session flags
        private const uint FWPM_SESSION_FLAG_DYNAMIC = 0x00000001;

        private const uint RPC_C_AUTHN_WINNT = 10;
        private const uint FWPM_SUBLAYER_FLAG_PERSISTENT = 0; // ours is dynamic

        // Filter weights on the FWP_UINT8 relative scale (0..15, higher evaluated
        // first within the sublayer). Permit must outrank block.
        private const ulong WEIGHT_BLOCK = 0;
        private const ulong WEIGHT_PERMIT = 15;

        // ===========================================================
        //  Public API (mirrors KillSwitch.Engage/Disengage)
        // ===========================================================

        public static void Engage(string endpointIp, string tunnelAddresses, ulong tunnelLuid)
        {
            if (_engaged) return;

            // Open with a DYNAMIC session: every filter/sublayer we add is then
            // automatically destroyed when the engine handle closes (and also if the
            // process dies). This is what makes teardown reliable — deleting a
            // sublayer does NOT remove the filters inside it, so without a dynamic
            // session the block-all filters would survive and leave no connectivity.
            FWPM_SESSION0 session = new FWPM_SESSION0();
            session.displayData.name = "WgSharp";
            session.displayData.description = "WgSharp kill-switch session";
            session.flags = FWPM_SESSION_FLAG_DYNAMIC;

            IntPtr sessPtr = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_SESSION0)));
            uint err;
            try
            {
                Marshal.StructureToPtr(session, sessPtr, false);
                err = FwpmEngineOpen0(null, RPC_C_AUTHN_WINNT, IntPtr.Zero, sessPtr, out _engine);
            }
            finally { Marshal.FreeHGlobal(sessPtr); }
            if (err != 0) throw new Exception("FwpmEngineOpen0 failed: " + err);

            err = FwpmTransactionBegin0(_engine, 0);
            if (err != 0) { CloseEngine(); throw new Exception("FwpmTransactionBegin0 failed: " + err); }

            try
            {
                _subLayerKey = Guid.NewGuid();
                AddSubLayer();
                L(WgSharp.Core.Logger.DebugMarker + "sublayer added.");

                // Permit traffic on the tunnel interface at ALL FOUR ALE layers
                // (outbound CONNECT + inbound RECV_ACCEPT, v4 + v6). This is the
                // traffic that's actually tunneled. Missing the RECV_ACCEPT layers
                // was why inbound flows were dropped.
                if (tunnelLuid != 0)
                {
                    AddPermitInterface(FWPM_LAYER_ALE_AUTH_CONNECT_V4, tunnelLuid);
                    AddPermitInterface(FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4, tunnelLuid);
                    AddPermitInterface(FWPM_LAYER_ALE_AUTH_CONNECT_V6, tunnelLuid);
                    AddPermitInterface(FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6, tunnelLuid);
                    L(WgSharp.Core.Logger.DebugMarker + "tunnel-interface permits added at 4 layers (LUID=" + tunnelLuid + ").");
                }
                else
                {
                    L("WFP: WARNING no tunnel LUID; tunneled traffic may be blocked.");
                }

                // Permit traffic to/from the VPN endpoint at all four layers so the
                // encrypted WireGuard packets (which leave from THIS process via the
                // physical adapter, not the tunnel) can flow both directions. This
                // stands in for the official client's app-ID permit: the endpoint is
                // the only remote our process talks to outside the tunnel.
                if (!string.IsNullOrEmpty(endpointIp))
                {
                    IPAddress ep;
                    if (IPAddress.TryParse(endpointIp, out ep))
                    {
                        bool v6 = ep.AddressFamily == AddressFamily.InterNetworkV6;
                        AddPermitRemoteAtLayer(ep, v6 ? FWPM_LAYER_ALE_AUTH_CONNECT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V4);
                        AddPermitRemoteAtLayer(ep, v6 ? FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6 : FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
                        L(WgSharp.Core.Logger.DebugMarker + "endpoint permits added (" + endpointIp + ").");
                    }
                }

                // Permit loopback and private LAN ranges at the CONNECT layers so
                // local resources and the gateway stay reachable.
                AddPermitRemotePrefix(IPAddress.Parse("127.0.0.0"), 8);
                AddPermitRemotePrefix(IPAddress.Parse("10.0.0.0"), 8);
                AddPermitRemotePrefix(IPAddress.Parse("172.16.0.0"), 12);
                AddPermitRemotePrefix(IPAddress.Parse("192.168.0.0"), 16);
                AddPermitRemotePrefix(IPAddress.Parse("224.0.0.0"), 4);
                AddPermitRemotePrefix(IPAddress.Parse("169.254.0.0"), 16);
                AddPermitRemotePrefix(IPAddress.Parse("::1"), 128);
                AddPermitRemotePrefix(IPAddress.Parse("fe80::"), 10);
                AddPermitRemotePrefix(IPAddress.Parse("ff00::"), 8);
                L(WgSharp.Core.Logger.DebugMarker + "loopback/LAN permits added.");

                // Block everything else at ALL FOUR layers (low weight).
                AddBlockAll(FWPM_LAYER_ALE_AUTH_CONNECT_V4);
                AddBlockAll(FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V4);
                AddBlockAll(FWPM_LAYER_ALE_AUTH_CONNECT_V6);
                AddBlockAll(FWPM_LAYER_ALE_AUTH_RECV_ACCEPT_V6);
                L(WgSharp.Core.Logger.DebugMarker + "block-all added at 4 layers.");

                err = FwpmTransactionCommit0(_engine);
                if (err != 0) throw new Exception("FwpmTransactionCommit0 failed: " + err);

                _engaged = true;
                L("WFP kill-switch engaged (leak-free, weighted filters).");
            }
            catch
            {
                try { FwpmTransactionAbort0(_engine); } catch { }
                CloseEngine();
                throw;
            }
        }

        public static void Disengage()
        {
            if (!_engaged && _engine == IntPtr.Zero) return;
            // With a dynamic session, closing the engine handle destroys every
            // filter and sublayer we added — reliable, complete teardown. (We tried
            // explicit sublayer deletion before, but deleting a sublayer does not
            // remove the filters inside it, which left the block-all active and the
            // machine with no connectivity.)
            CloseEngine();
            _engaged = false;
            L("WFP kill-switch disengaged (filters removed).");
        }

        private static void CloseEngine()
        {
            if (_engine != IntPtr.Zero)
            {
                try { FwpmEngineClose0(_engine); } catch { }
                _engine = IntPtr.Zero;
            }
        }

        // ===========================================================
        //  Filter / sublayer construction
        // ===========================================================

        private static void AddSubLayer()
        {
            FWPM_SUBLAYER0 sl = new FWPM_SUBLAYER0();
            sl.subLayerKey = _subLayerKey;
            sl.displayData.name = "WgSharp kill-switch";
            sl.displayData.description = "WgSharp WFP kill-switch sublayer";
            sl.flags = FWPM_SUBLAYER_FLAG_PERSISTENT; // 0 (dynamic via session)
            sl.weight = 0xFFFF; // max sublayer weight, so our filters win arbitration

            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_SUBLAYER0)));
            try
            {
                Marshal.StructureToPtr(sl, p, false);
                uint err = FwpmSubLayerAdd0(_engine, p, IntPtr.Zero);
                if (err != 0) throw new Exception("FwpmSubLayerAdd0 failed: " + err);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // Block-all filter at the given layer (no conditions => matches everything).
        private static void AddBlockAll(Guid layer)
        {
            FWPM_FILTER0 f = NewFilter(layer, "WgSharp block-all", FWP_ACTION_BLOCK, WEIGHT_BLOCK);
            f.numFilterConditions = 0;
            f.filterCondition = IntPtr.Zero;
            CommitFilter(ref f);
        }

        // Permit all traffic whose local interface is the given LUID (the tunnel
        // adapter). The condition value is an FWP_UINT64 pointing to the LUID.
        private static void AddPermitInterface(Guid layer, ulong luid)
        {
            FWPM_FILTER0 f = NewFilter(layer, "WgSharp permit tunnel-if", FWP_ACTION_PERMIT, WEIGHT_PERMIT);

            IntPtr luidBlob = Marshal.AllocHGlobal(8);
            Marshal.WriteInt64(luidBlob, unchecked((long)luid));

            FWPM_FILTER_CONDITION0 cond = new FWPM_FILTER_CONDITION0();
            cond.fieldKey = FWPM_CONDITION_IP_LOCAL_INTERFACE;
            cond.matchType = FWP_MATCH_EQUAL;
            cond.conditionValue.type = FWP_UINT64;     // value is a UINT64*
            cond.conditionValue.anonymous = luidBlob;  // pointer to the LUID

            IntPtr condArray = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION0)));
            Marshal.StructureToPtr(cond, condArray, false);
            f.numFilterConditions = 1;
            f.filterCondition = condArray;
            try { CommitFilter(ref f); }
            finally { Marshal.FreeHGlobal(condArray); Marshal.FreeHGlobal(luidBlob); }
        }

        private static void AddPermitRemotePrefix(IPAddress addr, int prefix)
        {
            bool v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
            Guid layer = v6 ? FWPM_LAYER_ALE_AUTH_CONNECT_V6 : FWPM_LAYER_ALE_AUTH_CONNECT_V4;
            AddPermitRemotePrefixAtLayer(addr, prefix, layer);
        }

        // Permit a remote address (full /32 or /128) at a specific layer — used to
        // permit the endpoint at both CONNECT and RECV_ACCEPT.
        private static void AddPermitRemoteAtLayer(IPAddress addr, Guid layer)
        {
            int prefix = addr.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            AddPermitRemotePrefixAtLayer(addr, prefix, layer);
        }

        private static void AddPermitRemotePrefixAtLayer(IPAddress addr, int prefix, Guid layer)
        {
            bool v6 = addr.AddressFamily == AddressFamily.InterNetworkV6;
            FWPM_FILTER0 f = NewFilter(layer, "WgSharp permit remote", FWP_ACTION_PERMIT, WEIGHT_PERMIT);

            IntPtr condArray;
            IntPtr valueBlob;
            BuildAddrCondition(FWPM_CONDITION_IP_REMOTE_ADDRESS, addr, prefix, v6, out condArray, out valueBlob);
            f.numFilterConditions = 1;
            f.filterCondition = condArray;
            try { CommitFilter(ref f); }
            finally { Marshal.FreeHGlobal(condArray); if (valueBlob != IntPtr.Zero) Marshal.FreeHGlobal(valueBlob); }
        }

        // Build a single FWPM_FILTER_CONDITION0 matching an address/prefix, marshalled
        // into unmanaged memory. Returns a pointer to the condition (array of 1) and
        // the separately-allocated address+mask blob (caller frees both).
        private static void BuildAddrCondition(Guid conditionKey, IPAddress addr, int prefix,
                                               bool v6, out IntPtr condArray, out IntPtr valueBlob)
        {
            byte[] raw = addr.GetAddressBytes();

            if (!v6)
            {
                // FWP_V4_ADDR_AND_MASK { UINT32 addr; UINT32 mask; } in host order.
                uint a = (uint)((raw[0] << 24) | (raw[1] << 16) | (raw[2] << 8) | raw[3]);
                uint mask = prefix == 0 ? 0u : 0xFFFFFFFFu << (32 - prefix);
                a &= mask;
                valueBlob = Marshal.AllocHGlobal(8);
                Marshal.WriteInt32(valueBlob, 0, unchecked((int)a));
                Marshal.WriteInt32(valueBlob, 4, unchecked((int)mask));
            }
            else
            {
                // FWP_V6_ADDR_AND_MASK { UINT8 addr[16]; UINT8 prefixLength; }
                valueBlob = Marshal.AllocHGlobal(17);
                Marshal.Copy(raw, 0, valueBlob, 16);
                Marshal.WriteByte(valueBlob, 16, (byte)prefix);
            }

            FWPM_FILTER_CONDITION0 cond = new FWPM_FILTER_CONDITION0();
            cond.fieldKey = conditionKey;
            cond.matchType = FWP_MATCH_EQUAL;
            cond.conditionValue.type = v6 ? FWP_V6_ADDR_MASK : FWP_V4_ADDR_MASK;
            cond.conditionValue.anonymous = valueBlob; // pointer to the addr/mask blob

            condArray = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER_CONDITION0)));
            Marshal.StructureToPtr(cond, condArray, false);
        }

        private static FWPM_FILTER0 NewFilter(Guid layer, string name, uint action, ulong weight)
        {
            FWPM_FILTER0 f = new FWPM_FILTER0();
            f.filterKey = Guid.NewGuid();
            f.displayData.name = name;
            f.displayData.description = name;
            f.layerKey = layer;
            f.subLayerKey = _subLayerKey;
            f.action.type = action;
            // Weight: use an inline FWP_UINT8 in the 0..15 relative-weight scale.
            // For UINT8 the value lives INSIDE the union (not via a pointer), so we
            // store it directly. permit=15 (evaluated first), block=0 (last).
            f.weight = MakeUint8Value((byte)weight);
            return f;
        }

        // FWP_VALUE0 holding an inline FWP_UINT8 (relative weight 0..15). The value
        // is stored directly in the union, NOT via a pointer.
        private static FWP_VALUE0 MakeUint8Value(byte v)
        {
            FWP_VALUE0 val = new FWP_VALUE0();
            val.type = FWP_UINT8; // 1 (corrected; 5 is FWP_INT8)
            val.anonymous = v;    // inline
            return val;
        }

        private static void CommitFilter(ref FWPM_FILTER0 f)
        {
            IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(FWPM_FILTER0)));
            try
            {
                Marshal.StructureToPtr(f, p, false);
                ulong id;
                uint err = FwpmFilterAdd0(_engine, p, IntPtr.Zero, out id);
                if (err != 0) throw new Exception("FwpmFilterAdd0 failed: " + err);
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ===========================================================
        //  Native structs (sequential layout, Unicode strings)
        // ===========================================================

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FWPM_DISPLAY_DATA0
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string name;
            [MarshalAs(UnmanagedType.LPWStr)] public string description;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FWPM_SESSION0
        {
            public Guid sessionKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public uint flags;
            public uint txnWaitTimeoutInMSec;
            public uint processId;
            public IntPtr sid;                                  // SID* (null)
            [MarshalAs(UnmanagedType.LPWStr)] public string username; // (null)
            public int kernelMode;                              // BOOL
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_VALUE0
        {
            public uint type;
            public long anonymous; // union (8 bytes on x64): inline value OR pointer
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_CONDITION_VALUE0
        {
            public uint type;
            public IntPtr anonymous; // pointer to FWP_V4/ V6 ADDR_AND_MASK, etc.
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWPM_ACTION0
        {
            public uint type;
            // Followed by a GUID (filterType / calloutKey) in a union; for BLOCK/
            // PERMIT it's unused, but the field must be present for correct size.
            public Guid filterType;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FWPM_FILTER_CONDITION0
        {
            public Guid fieldKey;
            public uint matchType;
            public FWP_CONDITION_VALUE0 conditionValue;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FWPM_SUBLAYER0
        {
            public Guid subLayerKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public uint flags;
            public IntPtr providerKey;   // GUID* (null)
            public FWP_BYTE_BLOB providerData;
            public ushort weight;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FWP_BYTE_BLOB
        {
            public uint size;
            public IntPtr data;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FWPM_FILTER0
        {
            public Guid filterKey;
            public FWPM_DISPLAY_DATA0 displayData;
            public uint flags;
            public IntPtr providerKey;        // GUID* (null)
            public FWP_BYTE_BLOB providerData;
            public Guid layerKey;
            public Guid subLayerKey;
            public FWP_VALUE0 weight;
            public uint numFilterConditions;
            public IntPtr filterCondition;    // FWPM_FILTER_CONDITION0*
            public FWPM_ACTION0 action;
            public IntPtr rawContextOrProviderContextKey; // union (UINT64 or GUID*) — null
            public IntPtr reserved;           // GUID* reserved
            public ulong filterId;            // out
            public FWP_VALUE0 effectiveWeight;// out
        }

        // ===========================================================
        //  fwpuclnt.dll imports
        // ===========================================================

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmEngineOpen0(
            [MarshalAs(UnmanagedType.LPWStr)] string serverName,
            uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engineHandle);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmEngineClose0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmTransactionBegin0(IntPtr engineHandle, uint flags);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmTransactionCommit0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmTransactionAbort0(IntPtr engineHandle);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmSubLayerAdd0(IntPtr engineHandle, IntPtr subLayer, IntPtr sd);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engineHandle, IntPtr key);

        [DllImport("fwpuclnt.dll")]
        private static extern uint FwpmFilterAdd0(IntPtr engineHandle, IntPtr filter, IntPtr sd, out ulong id);
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows.Forms;

namespace WgSharp.Ui
{
    /// <summary>
    /// Drives a webcam by hand-building a DirectShow capture graph (device
    /// source filter -> Sample Grabber -> Null Renderer) via raw COM interop.
    /// No external packages (matches this project's zero-dependency,
    /// direct-csc.exe build) - .NET Framework 4.8 has no managed DirectShow
    /// wrapper, so every interface/struct/GUID below is hand-declared.
    ///
    /// The DirectShow interop below is adapted from secile's UsbCamera.cs
    /// (MIT license, https://github.com/secile/UsbCamera) - a widely-used,
    /// single-file, dependency-free DirectShow capture implementation. This
    /// file trims that reference down to only what WgSharp needs (no
    /// still-image pin, no native preview-window rendering, no per-property
    /// camera controls, no explicit IAMStreamConfig format selection beyond
    /// committing the device's own default format - QrScanDialog draws its
    /// own preview from GrabFrame() and just wants whatever format the
    /// device already defaults to, converted to RGB24) and rewrites it for
    /// C# 5 (no auto-property initializers, no local functions - this
    /// project's build only supports up to C# 5).
    ///
    /// THREADING: no dedicated capture thread is needed. Building/tearing
    /// down the graph uses COM objects that expect an STA apartment, which
    /// the WinForms UI thread already is (WinForms' Main() carries
    /// [STAThread]) - so Start() runs synchronously on the caller's thread.
    /// The actual frame delivery (ISampleGrabberCB.BufferCB) is invoked by
    /// DirectShow's own internal streaming worker thread regardless of which
    /// thread built the graph; the callback here only copies bytes under a
    /// lock, which needs no COM apartment at all.
    ///
    /// KNOWN FAILURE MODE: on Windows 10/11, desktop (Win32) app camera
    /// access can be blocked system-wide by Settings -> Privacy & security ->
    /// Camera ("Camera access" / "Let desktop apps access your camera").
    /// When that's off, RenderStream may still succeed while the driver
    /// delivers only black frames or none at all. QrScanDialog detects both
    /// (no frames, or several consecutive blank frames) and points the user
    /// at that setting.
    /// </summary>
    public sealed class WebcamCapture : IDisposable
    {

        /// <summary>Optional diagnostic sink. QrScanDialog hooks this to forward camera
        /// internals into the app's Log tab. These lines are deliberately NOT
        /// debug-marked: camera capture on Windows is fragile enough (and hard
        /// enough to reproduce on a given machine) that the stage-by-stage
        /// trace is worth showing unconditionally.</summary>
        public event Action<string> Log;
        private void L(string m)
        {
            var h = Log;
            if (h != null) h(WgSharp.Core.Logger.Tag(m, "Camera"));
        }

        /// <summary>True if at least one video capture device is registered.</summary>
        public static bool AnyDriverAvailable()
        {
            string diagnostic;
            return AnyDriverAvailable(out diagnostic);
        }

        /// <summary>Same check as the no-arg overload, but also reports WHY it came
        /// back false - "enumeration found nothing" and "enumeration itself
        /// threw" look identical from a bare bool.</summary>
        public static bool AnyDriverAvailable(out string diagnostic)
        {
            try
            {
                List<string> names = EnumerateDeviceNames();
                if (names.Count > 0)
                {
                    diagnostic = "found " + names.Count + " device(s)";
                    return true;
                }
                diagnostic = "CLSID_VideoInputDeviceCategory enumeration returned no devices";
                return false;
            }
            catch (Exception ex)
            {
                diagnostic = "device enumeration threw: " + ex.Message;
                return false;
            }
        }

        /// <summary>Logs every video capture device Windows reports, for diagnostics.
        /// Instance method so it can use the Log event; safe to call before
        /// Start(). Returns the count found.</summary>
        public int LogAvailableDrivers()
        {
            try
            {
                List<string> names = EnumerateDeviceNames();
                if (names.Count == 0)
                {
                    L("No video capture devices reported (CLSID_VideoInputDeviceCategory enumeration empty).");
                }
                else
                {
                    for (int i = 0; i < names.Count; i++)
                        L("Capture device [" + i + "]: \"" + names[i] + "\"");
                    L(names.Count + " capture device(s) reported.");
                }
                return names.Count;
            }
            catch (Exception ex)
            {
                L("Device enumeration threw: " + ex.Message);
                return 0;
            }
        }

        /// <summary>Number of frames delivered so far. 0 well after Start() implies frames aren't flowing.</summary>
        public long FrameCount { get { lock (_frameLock) return _frameSeq; } }

        public void Start(Control host)
        {
            L("Building DirectShow capture graph (host " + host.ClientSize.Width + "x" + host.ClientSize.Height + ", unused for rendering - preview is drawn by QrScanDialog from GrabFrame()).");

            _graph = (DShow.IGraphBuilder)DShow.CoCreateInstance(DShow.CLSID_FilterGraph);
            _builder = (DShow.ICaptureGraphBuilder2)DShow.CoCreateInstance(DShow.CLSID_CaptureGraphBuilder2);
            ThrowIfFailed(_builder.SetFiltergraph(_graph), "ICaptureGraphBuilder2.SetFiltergraph");

            L("Creating video input device filter (index 0).");
            _sourceFilter = CreateVideoInputFilter(0);

            // Explicitly commit the device's own first advertised stream format via
            // IAMStreamConfig before the filter joins the graph. Some drivers only
            // actually start delivering samples once a format has been committed this
            // way, even when RenderStream's intelligent-connect alone already
            // negotiates a working connection.
            try
            {
                DShow.IPin outPin = FindOutputPin(_sourceFilter);
                DShow.IAMStreamConfig streamConfig = outPin as DShow.IAMStreamConfig;
                if (streamConfig != null)
                {
                    DShow.AM_MEDIA_TYPE mt0 = null;
                    int capCount = 0, capSize = 0;
                    streamConfig.GetNumberOfCapabilities(ref capCount, ref capSize);
                    IntPtr capBuf = capSize > 0 ? Marshal.AllocHGlobal(capSize) : IntPtr.Zero;
                    try
                    {
                        if (capCount > 0)
                        {
                            streamConfig.GetStreamCaps(0, ref mt0, capBuf);
                            ThrowIfFailed(streamConfig.SetFormat(mt0), "IAMStreamConfig.SetFormat");
                            L("Committed device's default stream format via IAMStreamConfig (format index 0).");
                        }
                    }
                    finally
                    {
                        if (capBuf != IntPtr.Zero) Marshal.FreeHGlobal(capBuf);
                        if (mt0 != null) DShow.DeleteMediaType(ref mt0);
                    }
                }
                Marshal.ReleaseComObject(outPin);
            }
            catch (Exception ex)
            {
                L("IAMStreamConfig commit skipped/failed (non-fatal, continuing): " + ex.Message);
            }

            ThrowIfFailed(((DShow.IFilterGraph)_graph).AddFilter(_sourceFilter, "VideoCapture"), "IFilterGraph.AddFilter(source)");

            L("Creating Sample Grabber (requesting RGB24 output).");
            object grabberObj = DShow.CoCreateInstance(DShow.CLSID_SampleGrabber);
            _grabberFilter = (DShow.IBaseFilter)grabberObj;
            DShow.ISampleGrabber grabber = (DShow.ISampleGrabber)grabberObj;
            DShow.AM_MEDIA_TYPE want = new DShow.AM_MEDIA_TYPE();
            want.MajorType = DShow.MEDIATYPE_Video;
            want.SubType = DShow.MEDIASUBTYPE_RGB24;
            ThrowIfFailed(grabber.SetMediaType(want), "ISampleGrabber.SetMediaType");
            ThrowIfFailed(grabber.SetBufferSamples(true), "ISampleGrabber.SetBufferSamples");
            ThrowIfFailed(((DShow.IFilterGraph)_graph).AddFilter(_grabberFilter, "SampleGrabber"), "IFilterGraph.AddFilter(grabber)");

            L("Creating Null Renderer.");
            _rendererFilter = (DShow.IBaseFilter)DShow.CoCreateInstance(DShow.CLSID_NullRenderer);
            ThrowIfFailed(((DShow.IFilterGraph)_graph).AddFilter(_rendererFilter, "NullRenderer"), "IFilterGraph.AddFilter(renderer)");

            L("Rendering the capture-pin stream through the Sample Grabber.");
            Guid category = DShow.PIN_CATEGORY_CAPTURE;
            Guid mediaType = DShow.MEDIATYPE_Video;
            int hr = _builder.RenderStream(ref category, ref mediaType, _sourceFilter, _grabberFilter, _rendererFilter);
            ThrowIfFailed(hr, "ICaptureGraphBuilder2.RenderStream");

            DShow.AM_MEDIA_TYPE connected = new DShow.AM_MEDIA_TYPE();
            ThrowIfFailed(grabber.GetConnectedMediaType(connected), "ISampleGrabber.GetConnectedMediaType");
            DShow.VIDEOINFOHEADER vih = (DShow.VIDEOINFOHEADER)Marshal.PtrToStructure(connected.pbFormat, typeof(DShow.VIDEOINFOHEADER));
            _frameWidth = vih.bmiHeader.biWidth;
            _frameHeight = Math.Abs(vih.bmiHeader.biHeight);
            _frameStride = ((_frameWidth * 3 + 3) / 4) * 4; // RGB24 rows are DWORD-aligned
            DShow.DeleteMediaType(ref connected);
            L("Negotiated video format: " + _frameWidth + "x" + _frameHeight + " (RGB24).");

            _callback = new SampleGrabberCallback(this);
            ThrowIfFailed(grabber.SetCallback(_callback, 1), "ISampleGrabber.SetCallback"); // 1 = BufferCB

            _mediaControl = (DShow.IMediaControl)_graph;
            int runHr = _mediaControl.Run();
            ThrowIfFailed(runHr, "IMediaControl.Run");
            if (runHr != 0)
            {
                // S_FALSE: the Stopped->Running transition is happening asynchronously.
                // Wait for it to actually complete rather than assuming Run() returning
                // without error means frames are already flowing.
                int state;
                _mediaControl.GetState(3000, out state);
                L("State transition was asynchronous (Run() returned S_FALSE); GetState(3000ms) -> " + state + " (2=Running).");
            }
            L("Graph running. Waiting for frames…");
        }

        public Bitmap GrabFrame()
        {
            byte[] buf; int w, h, stride;
            lock (_frameLock)
            {
                if (_latestFrame == null) return null;
                buf = _latestFrame;
                w = _frameWidth; h = _frameHeight; stride = _frameStride;
            }
            if (w <= 0 || h <= 0 || buf.Length < stride * h) return null;

            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h),
                    ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
                try
                {
                    // RGB24 from the Sample Grabber is a bottom-up DIB (row 0 = bottom of
                    // image) - flip while copying.
                    for (int y = 0; y < h; y++)
                    {
                        int srcRow = (h - 1 - y) * stride;
                        IntPtr dst = (IntPtr)(bd.Scan0.ToInt64() + y * bd.Stride);
                        Marshal.Copy(buf, srcRow, dst, Math.Min(stride, bd.Stride));
                    }
                }
                finally { bmp.UnlockBits(bd); }
                return bmp;
            }
            catch { return null; }
        }

        public void Dispose()
        {
            try { if (_mediaControl != null) _mediaControl.Stop(); } catch { }
            _callback = null;
            DShow.ReleaseInstance(ref _rendererFilter);
            DShow.ReleaseInstance(ref _grabberFilter);
            DShow.ReleaseInstance(ref _sourceFilter);
            DShow.ReleaseInstance(ref _builder);
            _mediaControl = null;
            DShow.ReleaseInstance(ref _graph);
            lock (_frameLock) { _latestFrame = null; }
        }

        // ---- Internals ----

        private DShow.IGraphBuilder _graph;
        private DShow.ICaptureGraphBuilder2 _builder;
        private DShow.IBaseFilter _sourceFilter;
        private DShow.IBaseFilter _grabberFilter;
        private DShow.IBaseFilter _rendererFilter;
        private DShow.IMediaControl _mediaControl;
        private SampleGrabberCallback _callback;

        private readonly object _frameLock = new object();
        private byte[] _latestFrame;
        private int _frameWidth;
        private int _frameHeight;
        private int _frameStride;
        private long _frameSeq;

        private static void ThrowIfFailed(int hr, string what)
        {
            if (hr < 0) throw new Exception(what + " failed: 0x" + hr.ToString("X8"));
        }

        /// <summary>Finds the first output pin on a filter (video capture sources expose their
        /// stream on pin 0 in practice for the simple single-pin devices this targets).</summary>
        private static DShow.IPin FindOutputPin(DShow.IBaseFilter filter)
        {
            DShow.IEnumPins pins = null;
            ThrowIfFailed(filter.EnumPins(out pins), "IBaseFilter.EnumPins");
            try
            {
                DShow.IPin pin = null;
                int fetched = 0;
                while (pins.Next(1, ref pin, ref fetched) == 0 && fetched > 0)
                {
                    DShow.PIN_DIRECTION dir;
                    int hr = pin.QueryDirection(out dir);
                    if (hr == 0 && dir == DShow.PIN_DIRECTION.PINDIR_OUTPUT) return pin;
                    Marshal.ReleaseComObject(pin);
                    pin = null;
                }
            }
            finally { Marshal.ReleaseComObject(pins); }
            throw new Exception("No output pin found on the video capture filter.");
        }

        /// <summary>Enumerates CLSID_VideoInputDeviceCategory monikers and reads each one's FriendlyName.</summary>
        private static List<string> EnumerateDeviceNames()
        {
            List<string> result = new List<string>();
            ForEachVideoInputMoniker(delegate (IMoniker moniker)
            {
                object value;
                DShow.IPropertyBag bag = BindToPropertyBag(moniker);
                try
                {
                    value = null;
                    bag.Read("FriendlyName", ref value, IntPtr.Zero);
                    result.Add(value as string ?? "(unnamed)");
                }
                finally { Marshal.ReleaseComObject(bag); }
                return false; // keep enumerating
            });
            return result;
        }

        /// <summary>Creates the IBaseFilter for the video input device at the given index.</summary>
        private static DShow.IBaseFilter CreateVideoInputFilter(int index)
        {
            DShow.IBaseFilter result = null;
            int current = 0;
            ForEachVideoInputMoniker(delegate (IMoniker moniker)
            {
                if (current++ != index) return false;
                Guid iid = DShow.IID_IBaseFilter;
                object filterObj;
                moniker.BindToObject(null, null, ref iid, out filterObj);
                result = (DShow.IBaseFilter)filterObj;
                return true; // stop
            });
            if (result == null) throw new Exception("No video capture device at index " + index + ".");
            return result;
        }

        private static DShow.IPropertyBag BindToPropertyBag(IMoniker moniker)
        {
            Guid iid = DShow.IID_IPropertyBag;
            object bagObj;
            moniker.BindToStorage(null, null, ref iid, out bagObj);
            return (DShow.IPropertyBag)bagObj;
        }

        private delegate bool MonikerVisitor(IMoniker moniker);

        private static void ForEachVideoInputMoniker(MonikerVisitor visit)
        {
            DShow.ICreateDevEnum devEnum = null;
            IEnumMoniker enumerator = null;
            try
            {
                devEnum = (DShow.ICreateDevEnum)DShow.CoCreateInstance(DShow.CLSID_SystemDeviceEnum);
                Guid category = DShow.CLSID_VideoInputDeviceCategory;
                int hr = devEnum.CreateClassEnumerator(ref category, out enumerator, 0);
                if (hr != 0 || enumerator == null) return; // no devices of this category

                IMoniker[] monikers = new IMoniker[1];
                IntPtr fetched = IntPtr.Zero;
                while (enumerator.Next(1, monikers, fetched) == 0)
                {
                    IMoniker m = monikers[0];
                    try
                    {
                        if (visit(m)) return;
                    }
                    finally { Marshal.ReleaseComObject(m); }
                }
            }
            finally
            {
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
                if (devEnum != null) Marshal.ReleaseComObject(devEnum);
            }
        }

        /// <summary>Receives ISampleGrabber's per-frame callback (invoked on DirectShow's own
        /// streaming thread, not the thread that built the graph).</summary>
        private sealed class SampleGrabberCallback : DShow.ISampleGrabberCB
        {
            private readonly WebcamCapture _owner;
            public SampleGrabberCallback(WebcamCapture owner) { _owner = owner; }

            public int SampleCB(double sampleTime, DShow.IMediaSample sample) { return 0; } // never called (registered for BufferCB)

            public int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen)
            {
                try
                {
                    if (pBuffer == IntPtr.Zero || bufferLen <= 0) return 0;
                    byte[] buf = new byte[bufferLen];
                    Marshal.Copy(pBuffer, buf, 0, bufferLen);

                    bool first;
                    lock (_owner._frameLock)
                    {
                        _owner._latestFrame = buf;
                        _owner._frameSeq++;
                        first = _owner._frameSeq == 1;
                    }
                    if (first) _owner.L("First frame delivered: " + _owner._frameWidth + "x" + _owner._frameHeight + ", " + bufferLen + " bytes.");
                }
                catch { /* never let an exception cross back into DirectShow's streaming thread */ }
                return 0;
            }
        }

        /// <summary>The hand-declared DirectShow COM interop surface this file needs.
        /// Adapted from secile's UsbCamera.cs (MIT license,
        /// https://github.com/secile/UsbCamera), trimmed to only what's
        /// called here and rewritten for C# 5.</summary>
        private static class DShow
        {
            public static object CoCreateInstance(Guid clsid)
            {
                return Activator.CreateInstance(Type.GetTypeFromCLSID(clsid));
            }

            public static void ReleaseInstance<T>(ref T com) where T : class
            {
                if (com != null) { Marshal.ReleaseComObject(com); com = null; }
            }

            public static void DeleteMediaType(ref AM_MEDIA_TYPE mt)
            {
                if (mt.cbFormat != 0 && mt.pbFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mt.pbFormat);
                if (mt.pUnk != IntPtr.Zero) Marshal.Release(mt.pUnk);
            }

            // ---- GUIDs (Filter Graph Manager, DirectShow's classic device
            // enumeration/capture pipeline - stable, well-documented CLSIDs
            // unchanged since DirectShow's introduction) ----
            public static readonly Guid CLSID_SystemDeviceEnum = new Guid("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
            public static readonly Guid CLSID_VideoInputDeviceCategory = new Guid("860BB310-5D01-11d0-BD3B-00A0C911CE86");
            public static readonly Guid CLSID_FilterGraph = new Guid("E436EBB3-524F-11CE-9F53-0020AF0BA770");
            public static readonly Guid CLSID_CaptureGraphBuilder2 = new Guid("BF87B6E1-8C27-11d0-B3F0-00AA003761C5");
            public static readonly Guid CLSID_SampleGrabber = new Guid("C1F400A0-3F08-11D3-9F0B-006008039E37");
            public static readonly Guid CLSID_NullRenderer = new Guid("C1F400A4-3F08-11D3-9F0B-006008039E37");

            public static readonly Guid IID_IBaseFilter = new Guid("56a86895-0ad4-11ce-b03a-0020af0ba770");
            public static readonly Guid IID_IPropertyBag = new Guid("55272A00-42CB-11CE-8135-00AA004BB851");

            public static readonly Guid MEDIATYPE_Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
            public static readonly Guid MEDIASUBTYPE_RGB24 = new Guid("E436EB7D-524F-11CE-9F53-0020AF0BA770");
            public static readonly Guid FORMAT_VideoInfo = new Guid("05589F80-C356-11CE-BF01-00AA0055595A");
            public static readonly Guid PIN_CATEGORY_CAPTURE = new Guid("fb6c4281-0353-11d1-905f-0000c0cc16ba");

            // ---- Interfaces (method lists trimmed to the prefix this file
            // actually calls - a [ComImport] interface only needs its
            // declared methods to match the real vtable's prefix) ----

            [ComImport, Guid("29840822-5B84-11D0-BD3B-00A0C911CE86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ICreateDevEnum
            {
                int CreateClassEnumerator(ref Guid pType, out IEnumMoniker ppEnumMoniker, int dwFlags);
            }

            [ComImport, Guid("55272A00-42CB-11CE-8135-00AA004BB851"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IPropertyBag
            {
                int Read([MarshalAs(UnmanagedType.LPWStr)] string propName, ref object var, IntPtr errorLog);
                int Write(string propName, ref object var);
            }

            [ComImport, Guid("56a8689f-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IFilterGraph
            {
                int AddFilter([In] IBaseFilter pFilter, [In, MarshalAs(UnmanagedType.LPWStr)] string pName);
                int RemoveFilter([In] IBaseFilter pFilter);
                int EnumFilters_Unused(); // placeholder: skipped, we never call it
                int FindFilterByName_Unused();
                int ConnectDirect_Unused();
                int Reconnect_Unused();
                int Disconnect_Unused();
                int SetDefaultSyncSource_Unused();
            }

            [ComImport, Guid("56a868a9-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IGraphBuilder
            {
                // IFilterGraph prefix (must match exactly, IGraphBuilder extends it)
                int AddFilter([In] IBaseFilter pFilter, [In, MarshalAs(UnmanagedType.LPWStr)] string pName);
                int RemoveFilter_Unused();
                int EnumFilters_Unused();
                int FindFilterByName_Unused();
                int ConnectDirect_Unused();
                int Reconnect_Unused();
                int Disconnect_Unused();
                int SetDefaultSyncSource_Unused();
                // IGraphBuilder's own extra methods - none called directly (we
                // cast to IFilterGraph/IMediaControl for what we need instead)
            }

            // NOTE: DirectShow's control interfaces (IMediaControl, IVideoWindow, IBasicAudio,
            // IBasicVideo, IMediaEventEx) are DUAL interfaces (IDispatch + vtable), unlike most
            // of DirectShow's other interfaces above which are pure IUnknown. Declaring one of
            // these as InterfaceIsIUnknown makes every call land on the wrong vtable slot -
            // IDispatch's own 4 methods (GetTypeInfoCount/GetTypeInfo/GetIDsOfNames/Invoke) sit
            // between IUnknown's 3 and the interface's own methods for a dual interface, so
            // "Run()" would silently end up calling GetTypeInfoCount instead. The misrouted
            // call doesn't obviously fail (no exception, a plausible-looking return value) -
            // the graph state just never transitions to Running, so zero frames ever arrive
            // with no clear error pointing at why. Must stay InterfaceIsDual.
            [ComImport, Guid("56a868b1-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
            public interface IMediaControl
            {
                int Run();
                int Pause_Unused();
                int Stop();
                int GetState(int msTimeout, out int pfs);
            }

            [ComImport, Guid("93E5A4E0-2D50-11d2-ABFA-00A0C9C6E38D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ICaptureGraphBuilder2
            {
                int SetFiltergraph([In] IGraphBuilder pfg);
                int GetFiltergraph_Unused();
                int SetOutputFileName_Unused();
                int FindInterface_Unused();
                int RenderStream([In] ref Guid pCategory, [In] ref Guid pType,
                    [In, MarshalAs(UnmanagedType.IUnknown)] object pSource,
                    [In] IBaseFilter pfCompressor, [In] IBaseFilter pfRenderer);
            }

            [ComImport, Guid("56a86895-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IBaseFilter
            {
                int GetClassID_Unused();
                int Stop_Unused();
                int Pause_Unused();
                int Run_Unused();
                int GetState_Unused();
                int SetSyncSource_Unused();
                int GetSyncSource_Unused();
                int EnumPins(out IEnumPins ppEnum);
                int FindPin_Unused();
                int QueryFilterInfo_Unused();
                int JoinFilterGraph_Unused();
                int QueryVendorInfo_Unused();
            }

            [ComImport, Guid("56a86892-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IEnumPins
            {
                int Next(int cPins, [In, Out] ref IPin ppPins, [In, Out] ref int pcFetched);
            }

            public enum PIN_DIRECTION { PINDIR_INPUT = 0, PINDIR_OUTPUT = 1 }

            [ComImport, Guid("56a86891-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IPin
            {
                int Connect_Unused();
                int ReceiveConnection_Unused();
                int Disconnect_Unused();
                int ConnectedTo_Unused();
                int ConnectionMediaType_Unused();
                int QueryPinInfo_Unused();
                int QueryDirection(out PIN_DIRECTION pPinDir);
            }

            [ComImport, Guid("C6E13340-30AC-11d0-A18C-00A0C9118956"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IAMStreamConfig
            {
                int SetFormat([In, MarshalAs(UnmanagedType.LPStruct)] AM_MEDIA_TYPE pmt);
                int GetFormat_Unused();
                int GetNumberOfCapabilities(ref int piCount, ref int piSize);
                int GetStreamCaps(int iIndex, [In, Out, MarshalAs(UnmanagedType.LPStruct)] ref AM_MEDIA_TYPE ppmt, IntPtr pSCC);
            }

            [ComImport, Guid("6B652FFF-11FE-4fce-92AD-0266B5D7C78F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ISampleGrabber
            {
                int SetOneShot_Unused();
                int SetMediaType([In, MarshalAs(UnmanagedType.LPStruct)] AM_MEDIA_TYPE pmt);
                int GetConnectedMediaType([In, Out, MarshalAs(UnmanagedType.LPStruct)] AM_MEDIA_TYPE pmt);
                int SetBufferSamples([In, MarshalAs(UnmanagedType.Bool)] bool bufferThem);
                int GetCurrentBuffer_Unused();
                int GetCurrentSample_Unused();
                int SetCallback([In] ISampleGrabberCB pCallback, [In] int whichMethodToCallback);
            }

            [ComImport, Guid("0579154A-2B53-4994-B0D0-E773148EFF85"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface ISampleGrabberCB
            {
                [PreserveSig] int SampleCB(double sampleTime, IMediaSample pSample);
                [PreserveSig] int BufferCB(double sampleTime, IntPtr pBuffer, int bufferLen);
            }

            [ComImport, Guid("56a8689a-0ad4-11ce-b03a-0020af0ba770"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IMediaSample { } // opaque: only ever passed through, never called

            [StructLayout(LayoutKind.Sequential), Serializable]
            public class AM_MEDIA_TYPE
            {
                public Guid MajorType;
                public Guid SubType;
                [MarshalAs(UnmanagedType.Bool)] public bool bFixedSizeSamples;
                [MarshalAs(UnmanagedType.Bool)] public bool bTemporalCompression;
                public uint lSampleSize;
                public Guid FormatType;
                public IntPtr pUnk;
                public uint cbFormat;
                public IntPtr pbFormat;
            }

            [StructLayout(LayoutKind.Sequential), Serializable]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential, Pack = 2), Serializable]
            public struct BITMAPINFOHEADER
            {
                public int biSize;
                public int biWidth;
                public int biHeight;
                public short biPlanes;
                public short biBitCount;
                public int biCompression;
                public int biSizeImage;
                public int biXPelsPerMeter;
                public int biYPelsPerMeter;
                public int biClrUsed;
                public int biClrImportant;
            }

            [StructLayout(LayoutKind.Sequential), Serializable]
            public struct VIDEOINFOHEADER
            {
                public RECT SrcRect;
                public RECT TrgRect;
                public int BitRate;
                public int BitErrorRate;
                public long AvgTimePerFrame;
                public BITMAPINFOHEADER bmiHeader;
            }
        }
    }
}

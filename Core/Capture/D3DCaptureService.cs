using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace FrameCastStudio.Core.Capture;

public sealed class GpuFrame
{
    internal Action<GpuFrame>? Returner;
    public ID3D11Texture2D Texture = null!;
    internal ID3D11VideoProcessorOutputView? OutView;
    public long TimestampHns;
    public void Release() => Returner?.Invoke(this);
}

public sealed class D3DCaptureService : IDisposable
{

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr hwnd, in Guid iid);
        IntPtr CreateForMonitor(IntPtr hmon, in Guid iid);
    }
    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess { IntPtr GetInterface(in Guid iid); }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr hstring, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int len, out IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint flags);

    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_ID3D11Texture2D = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private static IGraphicsCaptureItemInterop GetInterop()
    {
        WindowsCreateString("Windows.Graphics.Capture.GraphicsCaptureItem", 44, out var h);
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(h, typeof(IGraphicsCaptureItemInterop).GUID, out var f));
            return (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(f);
        }
        finally { WindowsDeleteString(h); }
    }

    public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var p = GetInterop().CreateForWindow(hwnd, IID_IGraphicsCaptureItem);
        return GraphicsCaptureItem.FromAbi(p);
    }
    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr hmon)
    {
        var p = GetInterop().CreateForMonitor(hmon, IID_IGraphicsCaptureItem);
        return GraphicsCaptureItem.FromAbi(p);
    }
    public static GraphicsCaptureItem CreateItem(CaptureSource src) =>
        src.Kind == SourceKind.Window ? CreateItemForWindow(src.Handle) : CreateItemForMonitor(src.Handle);

    public static IDirect3DDevice WrapAsWinRTDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var wp));
        return MarshalInterface<IDirect3DDevice>.FromAbi(wp);
    }

    public static ID3D11Texture2D GetUnderlyingTexture(IDirect3DSurface surface)
    {
        IntPtr surfPtr = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfPtr);
            var texPtr = access.GetInterface(IID_ID3D11Texture2D);
            return new ID3D11Texture2D(texPtr);
        }
        finally { Marshal.Release(surfPtr); }
    }

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    private readonly IDirect3DDevice _winrtDevice;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private GraphicsCaptureItem? _item;

    private ID3D11VideoDevice _vd = null!;
    private ID3D11VideoContext _vc = null!;
    private ID3D11VideoProcessor _vp = null!;
    private ID3D11VideoProcessorEnumerator _vpe = null!;
    private readonly System.Collections.Concurrent.ConcurrentQueue<GpuFrame> _free = new();
    private readonly List<GpuFrame> _all = new();
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inViews = new();
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];
    private readonly object _frameLock = new();
    private int _srcW, _srcH, _w, _h, _fps;
    private long _lastTs, _minIntervalHns;
    private bool _encode, _encodeErrLogged;

    public event Action<GpuFrame>? FrameReady;
    public PreviewRenderer? Preview;
    public long DroppedFrames;

    public long FramesCaptured;

    public long FramesEncoded;

    public long LastFrameTick = Environment.TickCount64;
    private Windows.Graphics.SizeInt32 _poolSize;
    private bool _bt2020;

    public D3DCaptureService()
    {

        D3D11.D3D11CreateDevice(null, DriverType.Hardware,
            DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev, out _, out var ctx).CheckError();
        Device = dev!; Context = ctx!;
        using (var mt = Device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);

        using var dxgi = Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var wp));
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(wp);
    }

    public (int w, int h) Size => (_w, _h);
    public (int w, int h) SourceSize => (_srcW, _srcH);

    public void Start(GraphicsCaptureItem item, int targetFps, bool cursor = true, int scalePercent = 100, bool bt2020 = false, bool disableBorder = true, bool encode = true)
    {
        targetFps = Math.Max(1, targetFps);
        _item = item; _fps = targetFps; _bt2020 = bt2020; _poolSize = item.Size; _encode = encode;
        _minIntervalHns = 10_000_000L / targetFps - 10_000_000L / (targetFps * 20);
        _srcW = item.Size.Width & ~1; _srcH = item.Size.Height & ~1;
        double k = Math.Clamp(scalePercent, 25, 100) / 100.0;
        _w = Math.Max(2, (int)(_srcW * k)) & ~1; _h = Math.Max(2, (int)(_srcH * k)) & ~1;

        if (_encode)
        {
            BuildVideoProcessor();
            for (int i = 0; i < 4; i++) { var f = NewNv12Frame(); _all.Add(f); _free.Enqueue(f); }
        }

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, item.Size);
        _pool.FrameArrived += OnFrameArrived;
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = cursor;
        if (disableBorder) { try { _session.IsBorderRequired = false; } catch {  } }
        TrySetMinUpdateInterval(_session);
        _session.StartCapture();
    }

    private GpuFrame NewNv12Frame()
    {
        var tex = Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_w, Height = (uint)_h, MipLevels = 1, ArraySize = 1, Format = Format.NV12,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        });
        var ovd = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        return new GpuFrame { Texture = tex, OutView = _vd.CreateVideoProcessorOutputView(tex, _vpe, ovd), Returner = f => _free.Enqueue(f) };
    }

    private static void TrySetMinUpdateInterval(GraphicsCaptureSession s)
    {
        try { s.GetType().GetProperty("MinUpdateInterval")?.SetValue(s, TimeSpan.FromMilliseconds(1)); }
        catch {  }
    }

    private ID3D11VideoProcessorInputView GetInputView(ID3D11Texture2D src)
    {
        IntPtr key = src.NativePointer;
        if (_inViews.TryGetValue(key, out var v)) return v;
        if (_inViews.Count >= 12) ClearInputViews();
        var ivd = new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new() };
        v = _vd.CreateVideoProcessorInputView(src, _vpe, ivd);
        _inViews[key] = v;
        return v;
    }

    private void ClearInputViews()
    {
        foreach (var v in _inViews.Values) v.Dispose();
        _inViews.Clear();
    }

    private void BuildVideoProcessor()
    {
        _vd = Device.QueryInterface<ID3D11VideoDevice>();
        _vc = Context.QueryInterface<ID3D11VideoContext>();
        var d = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)_srcW, InputHeight = (uint)_srcH, OutputWidth = (uint)_w, OutputHeight = (uint)_h,
            Usage = VideoUsage.PlaybackNormal,
            InputFrameRate = new((uint)_fps, 1), OutputFrameRate = new((uint)_fps, 1),
        };
        _vpe = _vd.CreateVideoProcessorEnumerator(d);
        _vp = _vd.CreateVideoProcessor(_vpe, 0);

        using var vc1 = _vc.QueryInterface<ID3D11VideoContext1>();
        vc1.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.RgbFullG22NoneP709);
        vc1.VideoProcessorSetOutputColorSpace1(_vp, _bt2020 ? ColorSpaceType.YcbcrStudioG22LeftP2020 : ColorSpaceType.YcbcrStudioG22LeftP709);
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object _)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        Volatile.Write(ref LastFrameTick, Environment.TickCount64);

        var cs = frame.ContentSize;
        if (cs.Width <= 0 || cs.Height <= 0) return;
        if (cs.Width != _poolSize.Width || cs.Height != _poolSize.Height)
        {
            _poolSize = cs;
            lock (_frameLock) ClearInputViews();
            try { sender.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 3, cs); }
            catch (Exception ex) { FrameCastStudio.Core.Log.Error("Pool.Recreate", ex); }
            return;
        }

        var preview = Preview;
        bool wantPreview = preview is { Enabled: true };
        bool wantEncode = _encode && FrameReady != null;
        if (!wantPreview && !wantEncode) return;

        long ts = frame.SystemRelativeTime.Ticks;
        IntPtr surfPtr = MarshalInterface<IDirect3DSurface>.FromManaged(frame.Surface);
        IntPtr texPtr;
        try
        {
            var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(surfPtr);
            texPtr = access.GetInterface(IID_ID3D11Texture2D);
        }
        finally { Marshal.Release(surfPtr); }
        using var src = new ID3D11Texture2D(texPtr);

        lock (_frameLock)
        {
            Interlocked.Increment(ref FramesCaptured);

            if (wantPreview) preview!.Render(src);

            if (wantEncode) EncodeFrame(src, ts);
        }
    }

    private void EncodeFrame(ID3D11Texture2D src, long ts)
    {
        if (_lastTs != 0 && ts - _lastTs < _minIntervalHns) return;
        if (!_free.TryDequeue(out var dst)) { Interlocked.Increment(ref DroppedFrames); return; }

        try
        {
            _streams[0] = new VideoProcessorStream { Enable = true, InputSurface = GetInputView(src) };
            _vc.VideoProcessorBlt(_vp, dst.OutView!, 0, _streams);
        }
        catch (Exception ex)
        {
            dst.Release();
            Interlocked.Increment(ref DroppedFrames);
            if (!_encodeErrLogged) { _encodeErrLogged = true; FrameCastStudio.Core.Log.Error("VideoProcessorBlt", ex); }
            return;
        }

        dst.TimestampHns = ts; _lastTs = ts;
        Interlocked.Increment(ref FramesEncoded);

        var handler = FrameReady;
        if (handler is null) { dst.Release(); return; }
        try { handler(dst); }
        catch {  }
    }

    public void StopCapture()
    {
        try { _session?.Dispose(); } catch { }
        try { _pool?.Dispose(); } catch { }
        _session = null; _pool = null;
    }

    public void Dispose()
    {
        StopCapture();
        lock (_frameLock) ClearInputViews();
        foreach (var f in _all) { f.OutView?.Dispose(); f.Texture.Dispose(); }
        _all.Clear();
        while (_free.TryDequeue(out _)) { }
        _vp?.Dispose(); _vpe?.Dispose(); _vc?.Dispose(); _vd?.Dispose();
        Context.Dispose(); Device.Dispose();
    }
}

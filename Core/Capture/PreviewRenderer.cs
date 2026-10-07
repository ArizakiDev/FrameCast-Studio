using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace FrameCastStudio.Core.Capture;

public sealed class PreviewRenderer : IDisposable
{
    [ComImport, Guid("63AAD0B8-7C24-40FF-85A8-640D944CC325"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISwapChainPanelNative { [PreserveSig] int SetSwapChain(IntPtr swapChain); }

    public static readonly Guid PanelNativeIid = new("63AAD0B8-7C24-40FF-85A8-640D944CC325");

    private const int BufferCount = 3;

    public static (int w, int h) ComputeSize(int srcW, int srcH, int maxW = 1920)
    {
        double k = Math.Min(1.0, (double)maxW / srcW);
        return (Math.Max(2, (int)(srcW * k)) & ~1, Math.Max(2, (int)(srcH * k)) & ~1);
    }

    private readonly object _lock = new();
    private readonly IDXGISwapChain1 _sc;
    private readonly ID3D11VideoDevice _vd;
    private readonly ID3D11VideoContext _vc;
    private readonly ID3D11VideoProcessorEnumerator _vpe;
    private readonly ID3D11VideoProcessor _vp;

    private readonly Dictionary<IntPtr, ID3D11VideoProcessorInputView> _inViews = new();
    private readonly Dictionary<IntPtr, ID3D11VideoProcessorOutputView> _outViews = new();
    private readonly VideoProcessorStream[] _streams = new VideoProcessorStream[1];
    private ISwapChainPanelNative? _panel;
    private bool _disposed;
    private long _presented;
    public volatile bool Enabled = true;

    public long FramesPresented => Interlocked.Read(ref _presented);

    public PreviewRenderer(ID3D11Device dev, IntPtr panelNative, int srcW, int srcH, int w, int h, int fps)
    {
        using (var dxgiDev = dev.QueryInterface<IDXGIDevice>())
        using (var adapter = dxgiDev.GetAdapter())
        using (var factory = adapter.GetParent<IDXGIFactory2>())
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)w, Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = BufferCount,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _sc = factory.CreateSwapChainForComposition(dev, desc);
        }

        _vd = dev.QueryInterface<ID3D11VideoDevice>();
        using var ctx = dev.ImmediateContext;
        _vc = ctx.QueryInterface<ID3D11VideoContext>();
        var d = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputWidth = (uint)srcW, InputHeight = (uint)srcH, OutputWidth = (uint)w, OutputHeight = (uint)h,
            Usage = VideoUsage.PlaybackNormal,
            InputFrameRate = new((uint)fps, 1), OutputFrameRate = new((uint)fps, 1),
        };
        _vpe = _vd.CreateVideoProcessorEnumerator(d);
        _vp = _vd.CreateVideoProcessor(_vpe, 0);
        using (var vc1 = _vc.QueryInterface<ID3D11VideoContext1>())
        {
            vc1.VideoProcessorSetStreamColorSpace1(_vp, 0, ColorSpaceType.RgbFullG22NoneP709);
            vc1.VideoProcessorSetOutputColorSpace1(_vp, ColorSpaceType.RgbFullG22NoneP709);
        }

        _panel = (ISwapChainPanelNative)Marshal.GetObjectForIUnknown(panelNative);
        Marshal.ThrowExceptionForHR(_panel.SetSwapChain(_sc.NativePointer));
    }

    public void Render(ID3D11Texture2D src)
    {
        if (_disposed || !Enabled) return;
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var bb = _sc.GetBuffer<ID3D11Texture2D>(0);
                _streams[0] = new VideoProcessorStream { Enable = true, InputSurface = InputView(src) };
                _vc.VideoProcessorBlt(_vp, OutputView(bb), 0, _streams);

                var hr = _sc.Present(0, PresentFlags.DoNotWait);
                if (hr.Success) Interlocked.Increment(ref _presented);
            }
            catch {  }
        }
    }

    private ID3D11VideoProcessorInputView InputView(ID3D11Texture2D src)
    {
        IntPtr key = src.NativePointer;
        if (_inViews.TryGetValue(key, out var v)) return v;
        if (_inViews.Count >= 12) { foreach (var o in _inViews.Values) o.Dispose(); _inViews.Clear(); }
        var ivd = new VideoProcessorInputViewDescription { FourCC = 0, ViewDimension = VideoProcessorInputViewDimension.Texture2D, Texture2D = new() };
        return _inViews[key] = _vd.CreateVideoProcessorInputView(src, _vpe, ivd);
    }

    private ID3D11VideoProcessorOutputView OutputView(ID3D11Texture2D backBuffer)
    {
        IntPtr key = backBuffer.NativePointer;
        if (_outViews.TryGetValue(key, out var v)) return v;
        if (_outViews.Count >= BufferCount + 2) { foreach (var o in _outViews.Values) o.Dispose(); _outViews.Clear(); }
        var ovd = new VideoProcessorOutputViewDescription { ViewDimension = VideoProcessorOutputViewDimension.Texture2D };
        return _outViews[key] = _vd.CreateVideoProcessorOutputView(backBuffer, _vpe, ovd);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try { _panel?.SetSwapChain(IntPtr.Zero); } catch { }
            if (_panel != null) { Marshal.ReleaseComObject(_panel); _panel = null; }
            foreach (var v in _inViews.Values) v.Dispose();
            foreach (var v in _outViews.Values) v.Dispose();
            _inViews.Clear(); _outViews.Clear();
            _vp.Dispose(); _vpe.Dispose(); _vc.Dispose(); _vd.Dispose(); _sc.Dispose();
        }
    }
}

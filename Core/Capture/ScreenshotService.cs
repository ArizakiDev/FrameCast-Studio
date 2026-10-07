using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace FrameCastStudio.Core.Capture;

public static class ScreenshotService
{
    public sealed record Result(string FilePath, int Width, int Height);

    public static async Task<Result> CaptureAsync(CaptureSource source, string folder, bool cursor, bool copyToClipboard, string format = "PNG")
    {
        Directory.CreateDirectory(folder);

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out var dev, out _, out _).CheckError();
        using var device = dev!;
        var winrtDevice = D3DCaptureService.WrapAsWinRTDevice(device);

        var item = D3DCaptureService.CreateItem(source);
        var size = item.Size;

        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
        using var session = pool.CreateCaptureSession(item);
        try { session.IsCursorCaptureEnabled = cursor; } catch { }
        try { session.IsBorderRequired = false; } catch { }

        var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>();
        pool.FrameArrived += (p, _) =>
        {
            var f = p.TryGetNextFrame();
            if (f != null) tcs.TrySetResult(f);
        };
        session.StartCapture();
        using var frame = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Dispose();

        using var src = D3DCaptureService.GetUnderlyingTexture(frame.Surface);
        int w = frame.ContentSize.Width, h = frame.ContentSize.Height;

        var staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        using (staging)
        {
            device.ImmediateContext.CopyResource(staging, src);
            var map = device.ImmediateContext.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var pixels = new byte[w * h * 4];
                unsafe
                {
                    byte* row = (byte*)map.DataPointer;
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy((IntPtr)(row + y * map.RowPitch), pixels, y * w * 4, w * 4);
                    }
                }

                var bmp = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Ignore);
                bmp.CopyFromBuffer(pixels.AsBuffer());

                var (encoderId, ext) = format switch
                {
                    "JPEG" => (BitmapEncoder.JpegEncoderId, "jpg"),
                    "BMP" => (BitmapEncoder.BmpEncoderId, "bmp"),
                    _ => (BitmapEncoder.PngEncoderId, "png"),
                };
                string path = Path.Combine(folder, $"FrameCast_{DateTime.Now:yyyyMMdd_HHmmss_fff}.{ext}");
                var file = await StorageFile.GetFileFromPathAsync(await EnsureFileAsync(path));
                using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);
                    encoder.SetSoftwareBitmap(bmp);
                    await encoder.FlushAsync();
                }

                if (copyToClipboard)
                {
                    try
                    {
                        var dp = new DataPackage();
                        dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
                        Clipboard.SetContent(dp);
                    }
                    catch {  }
                }

                return new Result(path, w, h);
            }
            finally { device.ImmediateContext.Unmap(staging, 0); }
        }
    }

    private static async Task<string> EnsureFileAsync(string path)
    {
        if (!File.Exists(path)) File.WriteAllBytes(path, Array.Empty<byte>());
        await Task.CompletedTask;
        return path;
    }
}

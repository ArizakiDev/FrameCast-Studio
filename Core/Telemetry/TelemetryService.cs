using System.Diagnostics;

namespace FrameCastStudio.Core.Telemetry;

public readonly record struct TelemetrySnapshot(double CpuPct, double GpuPct, double CaptureFps, double OutputFps, double PreviewFps,
                                                double RamMb, double BitrateKbps, long Dropped);

public sealed class TelemetryService : IDisposable
{
    private readonly Process _p = Process.GetCurrentProcess();
    private readonly GpuSampler _gpu = new();
    private readonly object _gate = new();
    private System.Threading.Timer? _timer;
    private TelemetrySnapshot _latest;
    private int _busy;
    private bool _disposed;

    private TimeSpan _lastCpu; private long _lastTicks;
    private long _lastCapture, _lastOutput, _lastPreview, _lastBytes;
    private long _bytes;

    public Func<long> CaptureFrames = () => 0;
    public Func<long> OutputFrames = () => 0;
    public Func<long> PreviewFrames = () => 0;
    public Func<long> Dropped = () => 0;

    public Func<long> BytesSent;

    public TelemetryService() { BytesSent = () => Interlocked.Read(ref _bytes); }

    public void AddBytes(long n) => Interlocked.Add(ref _bytes, n);

    public TelemetrySnapshot Latest { get { lock (_gate) return _latest; } }

    public void Start()
    {
        if (_timer != null || _disposed) return;
        _p.Refresh();
        _lastCpu = _p.TotalProcessorTime;
        _lastTicks = Stopwatch.GetTimestamp();
        _lastCapture = CaptureFrames(); _lastOutput = OutputFrames(); _lastPreview = PreviewFrames(); _lastBytes = BytesSent();
        _timer = new System.Threading.Timer(_ => Tick(), null, 1000, 1000);
    }

    private void Tick()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            long now = Stopwatch.GetTimestamp();
            double dt = (now - _lastTicks) / (double)Stopwatch.Frequency;
            if (dt <= 0.05) return;
            _lastTicks = now;

            _p.Refresh();
            var cpu = _p.TotalProcessorTime;
            double cpuPct = (cpu - _lastCpu).TotalSeconds / dt / Environment.ProcessorCount * 100.0;
            _lastCpu = cpu;

            long cap = CaptureFrames(), outp = OutputFrames(), prev = PreviewFrames(), bytes = BytesSent();
            double capFps = (cap - _lastCapture) / dt, outFps = (outp - _lastOutput) / dt, prevFps = (prev - _lastPreview) / dt;
            double kbps = Math.Max(0, (bytes - _lastBytes) * 8 / 1000.0 / dt);
            _lastCapture = cap; _lastOutput = outp; _lastPreview = prev; _lastBytes = bytes;

            var snap = new TelemetrySnapshot(
                Math.Clamp(cpuPct, 0, 100), _gpu.Sample(), Math.Max(0, capFps), Math.Max(0, outFps), Math.Max(0, prevFps),
                _p.WorkingSet64 / 1048576.0, kbps, Dropped());
            lock (_gate) _latest = snap;
        }
        catch (Exception ex) { Log.Write("Télémétrie : " + ex.Message, 1); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _timer?.Dispose(); } catch { }
        _gpu.Dispose();
    }

    private sealed class GpuSampler : IDisposable
    {
        private readonly string _prefix = $"pid_{Environment.ProcessId}_";
        private readonly Dictionary<string, PerformanceCounter> _counters = new();
        private long _lastRefresh = long.MinValue;
        private bool _unavailable;

        public double Sample()
        {
            if (_unavailable) return 0;
            try
            {
                long t = Environment.TickCount64;
                if (_lastRefresh == long.MinValue || t - _lastRefresh >= 4000) { Refresh(); _lastRefresh = t; }

                var perEngine = new Dictionary<string, double>();
                List<string>? dead = null;
                foreach (var (name, c) in _counters)
                {
                    try
                    {
                        double v = c.NextValue();
                        int e = name.IndexOf("_eng_", StringComparison.Ordinal), ty = name.IndexOf("engtype_", StringComparison.Ordinal);
                        string key = (e > 0 ? name[..e] : name) + "|" + (ty >= 0 ? name[(ty + 8)..] : "");
                        perEngine[key] = perEngine.GetValueOrDefault(key) + v;
                    }
                    catch { (dead ??= new()).Add(name); }
                }
                if (dead != null) foreach (var n in dead) { _counters[n].Dispose(); _counters.Remove(n); }
                return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
            }
            catch { return 0; }
        }

        private void Refresh()
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine")) { _unavailable = true; return; }
            var names = new PerformanceCounterCategory("GPU Engine").GetInstanceNames()
                .Where(n => n.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet();

            foreach (var gone in _counters.Keys.Where(k => !names.Contains(k)).ToList())
            { _counters[gone].Dispose(); _counters.Remove(gone); }

            foreach (var n in names)
            {
                if (_counters.ContainsKey(n)) continue;
                try
                {
                    var c = new PerformanceCounter("GPU Engine", "Utilization Percentage", n, readOnly: true);
                    c.NextValue();
                    _counters[n] = c;
                }
                catch {  }
            }
        }

        public void Dispose() { foreach (var c in _counters.Values) c.Dispose(); _counters.Clear(); }
    }
}

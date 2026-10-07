namespace FrameCastStudio.Core.Audio;

internal static class Db
{
    public static float ToLin(float db) => MathF.Pow(10f, db / 20f);
    public static float ToDb(float lin) => lin <= 1e-6f ? -120f : 20f * MathF.Log10(lin);

    public static float Coef(float ms, int sr) => ms <= 0.01f ? 0f : MathF.Exp(-1f / (ms * 0.001f * sr));
}

internal sealed class Biquad2
{
    private float _b0, _b1, _b2, _a1, _a2, _z1L, _z2L, _z1R, _z2R, _hz = -1; private int _sr;

    public void Set(float hz, int sr)
    {
        if (hz == _hz && sr == _sr) return;
        _hz = hz; _sr = sr;
        double w0 = 2 * Math.PI * Math.Clamp(hz, 10, sr * 0.45) / sr, cos = Math.Cos(w0), sin = Math.Sin(w0);
        double alpha = sin / (2 * 0.70710678), a0 = 1 + alpha;
        _b0 = (float)((1 + cos) / 2 / a0); _b1 = (float)(-(1 + cos) / a0); _b2 = _b0;
        _a1 = (float)(-2 * cos / a0); _a2 = (float)((1 - alpha) / a0);
    }

    public void Process(float[] buf, int n)
    {
        for (int i = 0; i + 1 < n; i += 2)
        {
            float x = buf[i], y = _b0 * x + _z1L;
            _z1L = _b1 * x - _a1 * y + _z2L; _z2L = _b2 * x - _a2 * y; buf[i] = y;
            x = buf[i + 1]; y = _b0 * x + _z1R;
            _z1R = _b1 * x - _a1 * y + _z2R; _z2R = _b2 * x - _a2 * y; buf[i + 1] = y;
        }
    }
}

internal sealed class DelayLine
{
    private float[] _ring = Array.Empty<float>(); private int _pos;

    public void Process(float[] buf, int n, int delayFrames)
    {
        if (delayFrames <= 0) { if (_ring.Length != 0) { _ring = Array.Empty<float>(); _pos = 0; } return; }
        int need = delayFrames * 2;
        if (_ring.Length != need) { _ring = new float[need]; _pos = 0; }
        for (int i = 0; i < n; i++)
        {
            float o = _ring[_pos]; _ring[_pos] = buf[i]; buf[i] = o;
            if (++_pos == _ring.Length) _pos = 0;
        }
    }
}

internal sealed class Gate
{
    private float _env, _gain = 1f; private int _hold;
    public bool Open { get; private set; } = true;

    public void Reset() { _env = 0; _gain = 1f; _hold = 0; Open = true; }

    public void Process(float[] buf, int n, int sr, float thrDb, float attMs, float relMs)
    {
        float thr = Db.ToLin(thrDb), close = thr * 0.708f;
        float ca = Db.Coef(attMs, sr), cr = Db.Coef(relMs, sr), ce = Db.Coef(30f, sr);
        int holdMax = sr / 20;
        for (int i = 0; i + 1 < n; i += 2)
        {
            float x = MathF.Max(MathF.Abs(buf[i]), MathF.Abs(buf[i + 1]));
            _env = x > _env ? x : _env * ce;
            if (Open) { if (_env < close) { if (--_hold <= 0) Open = false; } else _hold = holdMax; }
            else if (_env > thr) { Open = true; _hold = holdMax; }
            float t = Open ? 1f : 0f;
            _gain = t + (_gain - t) * (t > _gain ? ca : cr);
            buf[i] *= _gain; buf[i + 1] *= _gain;
        }
    }
}

internal sealed class Compressor
{
    private float _gr;
    public float GrDb => _gr;

    public void Reset() => _gr = 0f;

    public void Process(float[] buf, int n, int sr, float thrDb, float ratio, float attMs, float relMs, float makeupDb)
    {
        float ca = Db.Coef(attMs, sr), cr = Db.Coef(relMs, sr), mk = Db.ToLin(makeupDb);
        float slope = 1f - 1f / MathF.Max(1f, ratio);
        for (int i = 0; i + 1 < n; i += 2)
        {
            float x = MathF.Max(MathF.Abs(buf[i]), MathF.Abs(buf[i + 1]));
            float over = Db.ToDb(x) - thrDb;
            float target = over > 0f ? over * slope : 0f;
            _gr = target + (_gr - target) * (target > _gr ? ca : cr);
            float g = Db.ToLin(-_gr) * mk;
            buf[i] *= g; buf[i + 1] *= g;
        }
    }
}

internal sealed class Ducker
{
    private float _env, _gain = 1f;
    public void Reset() { _env = 0; _gain = 1f; }

    public float Process(float[] tgt, float[] key, int n, int sr, float thrDb, float amountDb, float attMs, float relMs)
    {
        float thr = Db.ToLin(thrDb), duck = Db.ToLin(-MathF.Abs(amountDb));
        float ca = Db.Coef(attMs, sr), cr = Db.Coef(relMs, sr), ce = Db.Coef(40f, sr);
        for (int i = 0; i + 1 < n; i += 2)
        {
            float x = MathF.Max(MathF.Abs(key[i]), MathF.Abs(key[i + 1]));
            _env = x > _env ? x : _env * ce;
            float t = _env > thr ? duck : 1f;
            _gain = t + (_gain - t) * (t < _gain ? ca : cr);
            tgt[i] *= _gain; tgt[i + 1] *= _gain;
        }
        return -Db.ToDb(_gain);
    }
}

internal sealed class Limiter
{
    private float _gain = 1f;

    public float Process(float[] buf, int n, int sr, float ceilingDb)
    {
        float ceil = Db.ToLin(MathF.Min(0f, ceilingDb)), cr = Db.Coef(80f, sr), minGain = 1f;
        for (int i = 0; i + 1 < n; i += 2)
        {
            float peak = MathF.Max(MathF.Abs(buf[i]), MathF.Abs(buf[i + 1]));
            float target = peak > ceil ? ceil / peak : 1f;
            _gain = 1f + (_gain - 1f) * cr;
            if (target < _gain) _gain = target;
            if (_gain < minGain) minGain = _gain;
            buf[i] *= _gain; buf[i + 1] *= _gain;
        }
        return -Db.ToDb(minGain);
    }
}

using System.Diagnostics;

namespace FrameCastStudio.Core;

public static class Clock
{
    private static readonly double Scale = 10_000_000.0 / Stopwatch.Frequency;
    public static long NowHns() => (long)(Stopwatch.GetTimestamp() * Scale);
}

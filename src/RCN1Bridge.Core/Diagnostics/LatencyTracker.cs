using System.Diagnostics;

namespace RCN1Bridge.Core.Diagnostics;

public sealed class LatencyTracker
{
    private readonly double[] _samples = new double[256];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;

    public void Record(long startTimestamp)
    {
        double ms = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
        lock (_gate)
        {
            _samples[_next] = ms;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length)
                _count++;
        }
    }

    public double? Median()
    {
        double[] copy;
        lock (_gate)
        {
            if (_count == 0)
                return null;
            copy = _samples[.._count];
        }
        Array.Sort(copy);
        return copy[copy.Length / 2];
    }

    public void Clear()
    {
        lock (_gate)
            _count = _next = 0;
    }
}

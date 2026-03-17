// Live/RollingWindowCounter.cs
using System;
using System.Runtime.CompilerServices;
using System.Threading;


namespace Miningcore.Live;

public sealed class RollingWindowCounter
{
    private readonly int[] buckets;
    private readonly int windowSec;
    private long lastEpochSec;
    private long total;
    private int lastIdx;

    public RollingWindowCounter(int windowSec)
    {
        this.windowSec = Math.Max(1, windowSec);
        buckets = new int[this.windowSec];
        lastEpochSec = CurrentSec();
        lastIdx = (int)(lastEpochSec % this.windowSec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long CurrentSec() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public void Add(int n = 1)
    {
        var now = CurrentSec();
        var idxNow = (int)(now % windowSec);

        var last = Interlocked.Read(ref lastEpochSec);
        if (now != last)
        {
            var lastIdxLocal = Volatile.Read(ref lastIdx);
            var steps = Math.Min(windowSec, (int)(now - last));
            for (int i = 1; i <= steps; i++)
            {
                lastIdxLocal = (lastIdxLocal + 1) % windowSec;
                var old = Interlocked.Exchange(ref buckets[lastIdxLocal], 0);
                Interlocked.Add(ref total, -old);
            }
            Volatile.Write(ref lastIdx, idxNow);
            Interlocked.Exchange(ref lastEpochSec, now);
        }

        Interlocked.Add(ref buckets[idxNow], n);
        Interlocked.Add(ref total, n);
    }

    public int Sum() => (int)Volatile.Read(ref total);
}

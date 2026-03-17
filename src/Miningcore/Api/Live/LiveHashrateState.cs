// Miningcore/src/Miningcore/Live/LiveHashrateState.cs
using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

using System.Collections.Generic;
using System.Linq;

namespace Miningcore.Live;

/// <summary>
/// Lock-free live hashrate + presence with per-worker tracking (address.miner)
/// - Rolling ring per second (~1800s window, 2048-slot ring) using fixed-point
/// - Timing Wheel eviction (buckets) O(1) per share, O(k) per minute
/// </summary>
public static class LiveHashrateState
{
    public const int DefaultWindowSec = 600; // 10 min
    public const int OnlineGraceSec = 180;

    private const int RingSize = 2048;
    private const int Mask = RingSize - 1;
    private const long SCALE = 1_000_000; // 6 decimals

    public sealed class RollingRing
    {
        private readonly long[] buckets = new long[RingSize];
        private readonly int[] secs = new int[RingSize];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(double amount)
        {
            // Backwards-compatible: still works without explicit timestamp
            var nowSec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            AddAt(amount, nowSec);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AddAt(double amount, int nowSec)
        {
            var idx = nowSec & Mask;

            // slot is from another second? discard
            if (Volatile.Read(ref secs[idx]) != nowSec)
            {
                Volatile.Write(ref secs[idx], nowSec);
                Interlocked.Exchange(ref buckets[idx], 0);
            }

            var inc = (long)Math.Round(amount * SCALE);
            Interlocked.Add(ref buckets[idx], inc);
        }

        public double SumWindow(int windowSec)
        {
            if (windowSec <= 0) windowSec = DefaultWindowSec;

            var nowSec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fromSec = nowSec - windowSec + 1;

            long acc = 0;
            for (var t = fromSec; t <= nowSec; t++)
            {
                var idx = t & Mask;
                if (Volatile.Read(ref secs[idx]) == t)
                    acc += Volatile.Read(ref buckets[idx]);
            }

            return acc / (double)SCALE;
        }

        /// <summary>
        /// Sum over window and also return ageSec:
        /// time elapsed (in seconds) since the first bucket with data in this window.
        /// ageSec == 0 means no data in this window.
        /// </summary>
        public (double sum, int ageSec) SumWindowWithAge(int windowSec)
        {
            if (windowSec <= 0) windowSec = DefaultWindowSec;

            var nowSec = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var fromSec = nowSec - windowSec + 1;

            long acc = 0;
            int firstSeen = 0;

            for (var t = fromSec; t <= nowSec; t++)
            {
                var idx = t & Mask;
                if (Volatile.Read(ref secs[idx]) == t)
                {
                    acc += Volatile.Read(ref buckets[idx]);

                    if (firstSeen == 0)
                        firstSeen = t;
                }
            }

            int ageSec = 0;

            if (firstSeen != 0)
            {
                ageSec = nowSec - firstSeen + 1;
                if (ageSec < 1)
                    ageSec = 1;
            }

            return (acc / (double)SCALE, ageSec);
        }
    }


    // ----------------- SHARDING -----------------
    private const int Shards = 64;

    private static readonly ConcurrentDictionary<string, RollingRing>[] PoolRings =
        Enumerable.Range(0, Shards)
            .Select(_ => new ConcurrentDictionary<string, RollingRing>(StringComparer.OrdinalIgnoreCase))
            .ToArray();

    private static readonly ConcurrentDictionary<(string poolId, string address, string miner), RollingRing>[] WorkerRings =
        Enumerable.Range(0, Shards)
            .Select(_ => new ConcurrentDictionary<(string, string, string), RollingRing>())
            .ToArray();

    private static readonly ConcurrentDictionary<(string poolId, string address, string miner), long>[] WorkerLastSeen =
        Enumerable.Range(0, Shards)
            .Select(_ => new ConcurrentDictionary<(string, string, string), long>())
            .ToArray();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShardOf(string s) => (s.GetHashCode() & 0x7fffffff) % Shards;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShardOf((string a, string b, string c) k)
    {
        unchecked
        {
            var h = k.a.GetHashCode();
            h = (h * 397) ^ k.b.GetHashCode();
            h = (h * 397) ^ k.c.GetHashCode();
            return (h & 0x7fffffff) % Shards;
        }
    }

    public static (double diffSum, long lastSeenMax, int ageSec) GetAddressWindowWithAge(
    string poolId, string address, int windowSec)
    {
        address ??= string.Empty;

        double acc = 0;
        long lastMax = 0;
        int maxAgeSec = 0;

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                if (kv.Key.poolId == poolId &&
                    string.Equals(kv.Key.address, address, StringComparison.OrdinalIgnoreCase))
                {
                    var (sum, age) = kv.Value.SumWindowWithAge(windowSec);
                    acc += sum;

                    if (WorkerLastSeen[i].TryGetValue(kv.Key, out var last) && last > lastMax)
                        lastMax = last;

                    if (age > maxAgeSec)
                        maxAgeSec = age;
                }
            }
        }

        return (acc, lastMax, maxAgeSec);
    }


    // ----------------- POOL-LEVEL -----------------
    public static RollingRing ForPool(string poolId)
    {
        var shard = ShardOf(poolId);
        return PoolRings[shard].GetOrAdd(poolId, _ => new RollingRing());
    }

    // ----------------- WORKER-LEVEL (address.miner) -----------------
    public static RollingRing ForWorker(string poolId, string address, string miner)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var key = (poolId, address, miner);
        var shard = ShardOf(key);
        return WorkerRings[shard].GetOrAdd(key, _ => new RollingRing());
    }

    public static void TouchWorker(string poolId, string address, string miner)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TouchWorker(poolId, address, miner, nowSec);
    }

    public static void TouchWorker(string poolId, string address, string miner, long nowSec)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var key = (poolId, address, miner);
        var shard = ShardOf(key);
        WorkerLastSeen[shard][key] = nowSec;
    }

    public static long? GetWorkerLastSeenSec(string poolId, string address, string miner)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var key = (poolId, address, miner);
        var shard = ShardOf(key);
        return WorkerLastSeen[shard].TryGetValue(key, out var sec) ? sec : (long?)null;
    }

    public static bool IsWorkerOnline(string poolId, string address, string miner, int? windowOverrideSec = null)
    {
        var last = GetWorkerLastSeenSec(poolId, address, miner);
        if (!last.HasValue) return false;

        var grace = Math.Max(windowOverrideSec ?? DefaultWindowSec, OnlineGraceSec);
        return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last.Value) <= grace;
    }

    public static IEnumerable<(string poolId, string address, string miner, RollingRing ring, long lastSeen)> EnumeratePoolWorkers(string poolId)
    {
        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                if (kv.Key.poolId == poolId)
                {
                    WorkerLastSeen[i].TryGetValue(kv.Key, out var last);
                    yield return (kv.Key.poolId, kv.Key.address, kv.Key.miner, kv.Value, last);
                }
            }
        }
    }

    public static IEnumerable<(string poolId, string address, string miner, RollingRing ring, long lastSeen)> EnumerateAllWorkers()
    {
        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                WorkerLastSeen[i].TryGetValue(kv.Key, out var last);
                yield return (kv.Key.poolId, kv.Key.address, kv.Key.miner, kv.Value, last);
            }
        }
    }

    // ----------------- ADDRESS-LEVEL AGGREGATES -----------------
    public static (double diffSum, long lastSeenMax) GetAddressWindow(string poolId, string address, int windowSec)
    {
        address ??= string.Empty;

        double acc = 0;
        long lastMax = 0;

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                if (kv.Key.poolId == poolId && string.Equals(kv.Key.address, address, StringComparison.OrdinalIgnoreCase))
                {
                    acc += kv.Value.SumWindow(windowSec);
                    if (WorkerLastSeen[i].TryGetValue(kv.Key, out var last) && last > lastMax)
                        lastMax = last;
                }
            }
        }

        return (acc, lastMax);
    }

    public static IEnumerable<(string address, double diffSum, long lastSeenMax)> EnumeratePoolAddresses(string poolId, int windowSec)
    {
        var map = new Dictionary<string, (double diff, long last)>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                if (kv.Key.poolId != poolId) continue;

                var addr = kv.Key.address;
                var add = kv.Value.SumWindow(windowSec);

                WorkerLastSeen[i].TryGetValue(kv.Key, out var last);
                if (map.TryGetValue(addr, out var cur))
                    map[addr] = (cur.diff + add, Math.Max(cur.last, last));
                else
                    map[addr] = (add, last);
            }
        }

        foreach (var kv in map)
            yield return (kv.Key, kv.Value.diff, kv.Value.last);
    }

    public static IEnumerable<(string worker, double diffSum, long lastSeenMax, int ageSec)>
    EnumerateAddressWorkersWithAge(string poolId, string address, int windowSec)
    {
        address ??= string.Empty;

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in WorkerRings[i])
            {
                if (kv.Key.poolId != poolId)
                    continue;

                if (!string.Equals(kv.Key.address, address, StringComparison.OrdinalIgnoreCase))
                    continue;

                var (sum, age) = kv.Value.SumWindowWithAge(windowSec);

                WorkerLastSeen[i].TryGetValue(kv.Key, out var last);
                yield return (kv.Key.miner, sum, last, age);
            }
        }
    }


    public static bool IsAddressOnline(string poolId, string address, int? windowOverrideSec = null)
    {
        var win = windowOverrideSec ?? DefaultWindowSec;
        var (_, last) = GetAddressWindow(poolId, address, win);
        if (last <= 0) return false;

        var grace = Math.Max(win, OnlineGraceSec);
        return (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - last) <= grace;
    }


    // ----------------- TIMING WHEEL EVICTION (per worker) -----------------
    private const int Buckets = 64;
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(32);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private static readonly ConcurrentQueue<ExpireToken>[] Wheel =
        Enumerable.Range(0, Buckets).Select(_ => new ConcurrentQueue<ExpireToken>()).ToArray();

    private static int currentBucketIdx = 0;

    private static readonly ConcurrentDictionary<(string poolId, string address, string miner), int> Gen =
        new ConcurrentDictionary<(string, string, string), int>();

    private static readonly Timer SweepTimer =
        new Timer(_ => SweepTickSafe(), null, SweepInterval, SweepInterval);

    private readonly struct ExpireToken
    {
        public readonly string PoolId;
        public readonly string Address;
        public readonly string Miner;
        public readonly int GenAtInsert;

        public ExpireToken(string poolId, string address, string miner, int gen)
        {
            PoolId = poolId; Address = address; Miner = miner; GenAtInsert = gen;
        }
    }

    /// <summary>Schedule eviction for a worker (call this on every share)</summary>
    public static void ScheduleExpiration(string poolId, string address, string miner)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ScheduleExpiration(poolId, address, miner, nowSec);
    }

    public static void ScheduleExpiration(string poolId, string address, string miner, long nowSec)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var key = (poolId, address, miner);
        var gen = Gen.AddOrUpdate(key, 1, static (_, old) => unchecked(old + 1));

        var expirySec = nowSec + (long)Ttl.TotalSeconds;
        var bucketIdx = (int)((expirySec / 60) & (Buckets - 1));

        Wheel[bucketIdx].Enqueue(new ExpireToken(poolId, address, miner, gen));
    }


    private static void SweepTickSafe()
    {
        try { SweepTick(); }
        catch { /* ignore/log */ }
    }

    private static int SweepTick()
    {
        var removed = 0;
        var now = DateTimeOffset.UtcNow;
        var idx = Interlocked.Increment(ref currentBucketIdx) & (Buckets - 1);

        while (Wheel[idx].TryDequeue(out var tok))
        {
            var last = GetWorkerLastSeenSec(tok.PoolId, tok.Address, tok.Miner);
            if (!last.HasValue) continue;

            var expired = (now.ToUnixTimeSeconds() - last.Value) >= (long)Ttl.TotalSeconds;

            if (expired && Gen.TryGetValue((tok.PoolId, tok.Address, tok.Miner), out var curGen) && curGen == tok.GenAtInsert)
            {
                if (TryRemoveWorker(tok.PoolId, tok.Address, tok.Miner))
                    removed++;
            }
        }

        return removed;
    }

    private static bool TryRemoveWorker(string poolId, string address, string miner)
    {
        address ??= string.Empty;
        miner ??= string.Empty;

        var key = (poolId, address, miner);
        var shard = ShardOf(key);

        var ok1 = WorkerLastSeen[shard].TryRemove(key, out _);
        var ok2 = WorkerRings[shard].TryRemove(key, out _);
        Gen.TryRemove(key, out _);

        return ok1 || ok2;
    }
}

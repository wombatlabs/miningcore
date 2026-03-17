using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Miningcore.Live;

/// <summary>
/// Live session stats per worker (address.worker) in memory:
/// - Counts accepted / rejected / stale shares since the worker "connected"
/// - Session lasts while the worker is active (TTL equal to LiveHashrateState)
/// - Clean when the worker expires in LiveHashrateState.TryRemoveWorker(..).
/// </summary>
public static class LiveSessionShareStatsState
{
    private const int Shards = 64;

    private sealed class SessionStats
    {
        public long Accepted;
        public long Rejected;
        public long Stale;
        public int FirstSeenSec;
        public int LastSeenSec;
    }

    private static readonly ConcurrentDictionary<(string poolId, string address, string worker), SessionStats>[] ShardedStats =
        CreateShards();

    private static ConcurrentDictionary<(string poolId, string address, string worker), SessionStats>[] CreateShards()
    {
        var arr = new ConcurrentDictionary<(string, string, string), SessionStats>[Shards];
        for (int i = 0; i < Shards; i++)
            arr[i] = new ConcurrentDictionary<(string, string, string), SessionStats>();
        return arr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ShardOf((string poolId, string address, string worker) key)
    {
        unchecked
        {
            var h = key.poolId?.GetHashCode() ?? 0;
            h = (h * 397) ^ (key.address?.GetHashCode() ?? 0);
            h = (h * 397) ^ (key.worker?.GetHashCode() ?? 0);
            return (h & 0x7fffffff) % Shards;
        }
    }

    /// <summary>
    /// Generic entrypoint para qualquer share.
    /// accepted = true  => accepted
    /// accepted = false + stale = true  => stale
    /// accepted = false + stale = false => rejected
    /// </summary>
    public static void OnShare(string poolId, string address, string worker, bool accepted, bool stale, long nowSecLong)
    {
        poolId ??= string.Empty;
        address ??= string.Empty;
        worker ??= string.Empty;

        var nowSec = (int)nowSecLong;
        var key = (poolId, address, worker);
        var shardIdx = ShardOf(key);
        var shard = ShardedStats[shardIdx];

        var stats = shard.GetOrAdd(key, _ => new SessionStats
        {
            FirstSeenSec = nowSec,
            LastSeenSec = nowSec
        });

        if (accepted)
            Interlocked.Increment(ref stats.Accepted);
        else if (stale)
            Interlocked.Increment(ref stats.Stale);
        else
            Interlocked.Increment(ref stats.Rejected);

        Volatile.Write(ref stats.LastSeenSec, nowSec);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnAccepted(string poolId, string address, string worker, long nowSecLong) =>
        OnShare(poolId, address, worker, accepted: true, stale: false, nowSecLong);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnRejected(string poolId, string address, string worker, long nowSecLong) =>
        OnShare(poolId, address, worker, accepted: false, stale: false, nowSecLong);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void OnStale(string poolId, string address, string worker, long nowSecLong) =>
        OnShare(poolId, address, worker, accepted: false, stale: true, nowSecLong);

    /// <summary>
    ///Call when the worker expires in LiveHashrateState.TryRemoveWorker.
    ///Kills the session (next share starts new session).
    /// </summary>
    public static void ResetWorker(string poolId, string address, string worker)
    {
        poolId ??= string.Empty;
        address ??= string.Empty;
        worker ??= string.Empty;

        var key = (poolId, address, worker);
        var shardIdx = ShardOf(key);
        var shard = ShardedStats[shardIdx];

        shard.TryRemove(key, out _);
    }

    public sealed class SessionSnapshot
    {
        public long Accepted { get; init; }
        public long Rejected { get; init; }
        public long Stale { get; init; }
        public int? FirstSeenSec { get; init; }
        public int? LastSeenSec { get; init; }
    }

    /// <summary>
    /// Aggregate session per address (sum all workers).
    /// Used on the main page of the miner.
    /// </summary>
    public static SessionSnapshot GetAddressSession(string poolId, string address)
    {
        poolId ??= string.Empty;
        address ??= string.Empty;

        long acc = 0;
        long rej = 0;
        long stale = 0;
        int? first = null;
        int? last = null;

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in ShardedStats[i])
            {
                if (!string.Equals(kv.Key.poolId, poolId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(kv.Key.address, address, StringComparison.OrdinalIgnoreCase))
                    continue;

                var s = kv.Value;

                acc += Volatile.Read(ref s.Accepted);
                rej += Volatile.Read(ref s.Rejected);
                stale += Volatile.Read(ref s.Stale);

                var f = Volatile.Read(ref s.FirstSeenSec);
                var l = Volatile.Read(ref s.LastSeenSec);

                if (f > 0)
                {
                    if (!first.HasValue || f < first.Value)
                        first = f;
                }

                if (l > 0)
                {
                    if (!last.HasValue || l > last.Value)
                        last = l;
                }
            }
        }

        return new SessionSnapshot
        {
            Accepted = acc,
            Rejected = rej,
            Stale = stale,
            FirstSeenSec = first,
            LastSeenSec = last
        };
    }

    /// <summary>
    /// Session per specific worker.
    /// Used on endpoint /miners/{address}/workers-lite.
    /// </summary>
    public static bool TryGetWorkerSession(string poolId, string address, string worker, out SessionSnapshot snapshot)
    {
        poolId ??= string.Empty;
        address ??= string.Empty;
        worker ??= string.Empty;

        var key = (poolId, address, worker);
        var shardIdx = ShardOf(key);
        var shard = ShardedStats[shardIdx];

        if (!shard.TryGetValue(key, out var s))
        {
            snapshot = null;
            return false;
        }

        snapshot = new SessionSnapshot
        {
            Accepted = Volatile.Read(ref s.Accepted),
            Rejected = Volatile.Read(ref s.Rejected),
            Stale = Volatile.Read(ref s.Stale),
            FirstSeenSec = s.FirstSeenSec > 0 ? s.FirstSeenSec : (int?)null,
            LastSeenSec = s.LastSeenSec > 0 ? s.LastSeenSec : (int?)null
        };

        return true;
    }

    /// <summary>
    /// List workers + session for an address.
    /// </summary>
    public static IEnumerable<(string worker, SessionSnapshot session)> EnumerateWorkersSessions(string poolId, string address)
    {
        poolId ??= string.Empty;
        address ??= string.Empty;

        var map = new Dictionary<string, SessionStats>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < Shards; i++)
        {
            foreach (var kv in ShardedStats[i])
            {
                if (!string.Equals(kv.Key.poolId, poolId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.Equals(kv.Key.address, address, StringComparison.OrdinalIgnoreCase))
                    continue;

                map[kv.Key.worker] = kv.Value;
            }
        }

        foreach (var kv in map)
        {
            var s = kv.Value;

            yield return (kv.Key, new SessionSnapshot
            {
                Accepted = Volatile.Read(ref s.Accepted),
                Rejected = Volatile.Read(ref s.Rejected),
                Stale = Volatile.Read(ref s.Stale),
                FirstSeenSec = s.FirstSeenSec > 0 ? s.FirstSeenSec : (int?)null,
                LastSeenSec = s.LastSeenSec > 0 ? s.LastSeenSec : (int?)null
            });
        }
    }
}

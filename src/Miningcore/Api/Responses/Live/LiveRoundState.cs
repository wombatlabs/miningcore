// Live/LiveRoundState.cs
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Miningcore.Live;

/// <summary>
/// Tracks current round per pool (shares since last block-candidate).
/// Reset on new candidate. Lightweight and in-memory only.
/// </summary>
public static class LiveRoundState
{
    public sealed class Round
    {
        public long StartedAtSec;     // epoch seconds
        public ulong? Height;         // last candidate height (optional)
        public long ActualShares;     // counted shares since StartedAt
    }

    private static readonly ConcurrentDictionary<string, Round> Rounds =
        new(StringComparer.OrdinalIgnoreCase);

    public static void AddShare(string poolId)
    {
        var r = Rounds.GetOrAdd(poolId, _ => new Round { StartedAtSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        Interlocked.Increment(ref r.ActualShares);
    }

    public static void OnBlockCandidate(string poolId, ulong? height = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Rounds.AddOrUpdate(poolId,
            _ => new Round { StartedAtSec = now, Height = height, ActualShares = 0 },
            (_, old) => { old.StartedAtSec = now; old.Height = height; Interlocked.Exchange(ref old.ActualShares, 0); return old; });
    }

    public static (DateTime StartedAt, ulong? Height, long ActualShares) Snapshot(string poolId)
    {
        var r = Rounds.GetOrAdd(poolId, _ => new Round { StartedAtSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        return (DateTimeOffset.FromUnixTimeSeconds(Volatile.Read(ref r.StartedAtSec)).UtcDateTime, r.Height, Volatile.Read(ref r.ActualShares));
    }
}

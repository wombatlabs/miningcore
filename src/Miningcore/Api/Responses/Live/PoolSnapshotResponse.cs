// src/Miningcore/Api/Responses/Live/PoolSnapshotResponse.cs
using System;
using System.Text.Json.Serialization;

namespace Miningcore.Api.Responses.Live;

public class PoolSnapshotResponse
{
    public string PoolId { get; set; }
    public DateTime AsOf { get; set; }
    public int WindowSec { get; set; } = 600;
    public string Unit { get; set; } = "H/s";

    public double CurrentHashrate { get; set; }
    public double SharesPerSec { get; set; }
    public int MinersOnline { get; set; }

    public PoolRoundInfo Round { get; set; }
    public PoolNetworkInfo Network { get; set; }

    public MinerNow[] TopMinersNow { get; set; } = Array.Empty<MinerNow>();

    [JsonPropertyName("hashrate")]
    public double Hashrate => CurrentHashrate;
}

public class PoolRoundInfo
{
    public ulong Height { get; set; }
    public DateTime? StartedAt { get; set; }
    public long ActualShares { get; set; }
    public double ExpectedShares { get; set; }
    public double LuckPercent { get; set; }
}

public class PoolNetworkInfo
{
    public ulong Height { get; set; }
    public double Difficulty { get; set; }
    public double Hashrate { get; set; }
}

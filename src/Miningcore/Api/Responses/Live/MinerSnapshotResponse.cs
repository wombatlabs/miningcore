// src/Miningcore/Api/Responses/Live/MinerSnapshotResponse.cs
using System;
using System.Text.Json.Serialization;

namespace Miningcore.Api.Responses.Live;

public class MinerSnapshotResponse
{
    public string PoolId { get; set; }
    public string Address { get; set; }
    public DateTime AsOf { get; set; }
    public int WindowSec { get; set; } = 600;
    public string Unit { get; set; } = "H/s";

    public bool Online { get; set; }
    public DateTime? LastShareAt { get; set; }

    public double CurrentHashrate { get; set; }
    public double SharesPerSec { get; set; }

    public double DifficultyAssigned { get; set; }
    public double RejectPercentWindow { get; set; }
    public double StalePercentWindow { get; set; }

    [JsonPropertyName("hashrate")]
    public double Hashrate => CurrentHashrate;
}

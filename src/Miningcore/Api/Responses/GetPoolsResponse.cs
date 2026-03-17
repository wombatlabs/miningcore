// src/Miningcore/Api/Responses/GetPoolsResponse.cs
using System.Text.Json; // para JsonElement
using System.Text.Json.Serialization;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Api.Responses;

// Coin metadata exposed via API
public class ApiCoinConfig
{
    public string Type { get; set; }
    public string Name { get; set; }
    public string Symbol { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Website { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Market { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Family { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Algorithm { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Twitter { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Discord { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Telegram { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Github { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string CanonicalName { get; set; }
}

public class ApiPoolPayoutSchemeConfig
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? Factor { get; set; } = 2.0m;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? BlockFinderPercentage { get; set; } = 5.0m;
}

public class ApiPoolPaymentProcessingConfig
{
    public bool Enabled { get; set; }

    // In pool base currency (e.g. BTC, not sats)
    public decimal MinimumPayment { get; set; }

    public string PayoutScheme { get; set; }

    // strongly-typed view model
    public ApiPoolPayoutSchemeConfig PayoutSchemeConfig { get; set; }

    // Keep passthrough for any extra fields from pool config (System.Text.Json)
    [JsonExtensionData]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

// Aggregated pool info returned from API
public partial class PoolInfo
{
    // Configuration (non-sensitive)
    public string Id { get; set; }

    public ApiCoinConfig Coin { get; set; }
    public Dictionary<int, PoolEndpoint> Ports { get; set; }
    public ApiPoolPaymentProcessingConfig PaymentProcessing { get; set; }
    public PoolShareBasedBanningConfig ShareBasedBanning { get; set; }
    public int ClientConnectionTimeout { get; set; }
    public int JobRebroadcastTimeout { get; set; }
    public int BlockRefreshInterval { get; set; }
    public float PoolFeePercent { get; set; }
    public string Address { get; set; }
    public string AddressInfoLink { get; set; }

    // Stats
    public PoolStats PoolStats { get; set; }
    public BlockchainStats NetworkStats { get; set; }
    public MinerPerformanceStats[] TopMiners { get; set; }

    public decimal TotalPaid { get; set; }
    public uint TotalBlocks { get; set; }
    public uint TotalConfirmedBlocks { get; set; }
    public uint TotalPendingBlocks { get; set; }
    public decimal BlockReward { get; set; }
    public DateTime? LastPoolBlockTime { get; set; }

    // Filled when available (see controllers)
    public double PoolEffort { get; set; }
}

public class GetPoolsResponse
{
    public PoolInfo[] Pools { get; set; }
}

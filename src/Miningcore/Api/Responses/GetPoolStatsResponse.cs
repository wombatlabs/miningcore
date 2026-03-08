namespace Miningcore.Api.Responses;

public partial class AggregatedPoolStats
{
    public double PoolHashrate { get; set; }
    public int ConnectedMiners { get; set; }
    public int TotalWallets { get; set; }
    public double ValidSharesPerSecond { get; set; }
    public double NetworkHashrate { get; set; }
    public double NetworkDifficulty { get; set; }

    public DateTime Created { get; set; }
}

public class GetPoolStatsResponse
{
    public AggregatedPoolStats[] Stats { get; set; }
}

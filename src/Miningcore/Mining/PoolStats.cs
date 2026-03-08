namespace Miningcore.Mining;

public class PoolStats
{
    public DateTime? LastPoolBlockTime { get; set; }
    public int ConnectedMiners { get; set; }
    public int TotalWallets { get; set; }
    public double PoolHashrate { get; set; }
    public double SharesPerSecond { get; set; }
}

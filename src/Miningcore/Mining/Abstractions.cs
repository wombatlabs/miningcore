using System.Collections.Generic;
using Miningcore.Blockchain;
using Miningcore.Configuration;

namespace Miningcore.Mining;

public class WorkerShareStats
{
    public string Miner { get; set; }
    public string Worker { get; set; }
    public int ValidShares { get; set; }
    public int InvalidShares { get; set; }
    public int StaleShares { get; set; }
}

public interface IMiningPool
{
    PoolConfig Config { get; }
    PoolStats PoolStats { get; }
    BlockchainStats NetworkStats { get; }
    double ShareMultiplier { get; }
    void Configure(PoolConfig pc, ClusterConfig cc);
    double HashrateFromShares(double shares, double interval);
    IReadOnlyCollection<WorkerShareStats> GetWorkerShareStats(string miner);
    Task RunAsync(CancellationToken ct);
}

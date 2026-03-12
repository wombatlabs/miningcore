namespace Miningcore.Persistence.Model.Projections;

public record MinerWorkerHashes
{
    public double Sum { get; init; }
    public long Count { get; init; }
    public string Miner { get; init; }
    public string Worker { get; init; }
    public DateTime FirstShare { get; init; }
    public DateTime LastShare { get; init; }
}

public record MinerWorkerHashrate
{
    public string Miner { get; init; }
    public string Worker { get; init; }
    public double Hashrate { get; init; }
}

public record MinerWorkerShareStats
{
    public string Worker { get; init; }
    public double? BestDifficulty { get; init; }
    public DateTime? LastSeen { get; init; }
}

public record MinerShareStats
{
    public string Miner { get; init; }
    public double? BestDifficulty { get; init; }
    public DateTime? LastSeen { get; init; }
}

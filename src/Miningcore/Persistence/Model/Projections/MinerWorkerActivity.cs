using System;

namespace Miningcore.Persistence.Model.Projections;

public record MinerWorkerActivity
{
    public string Miner { get; init; }
    public string Worker { get; init; }
    public DateTime FirstShare { get; init; }
    public DateTime LastShare { get; init; }
}

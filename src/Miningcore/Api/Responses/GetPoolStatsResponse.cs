// src/Miningcore/Api/Responses/GetPoolStatsResponse.cs
using System;

namespace Miningcore.Api.Responses
{
    public class AggregatedPoolStats
    {
        public double PoolHashrate { get; set; }
        public int ConnectedMiners { get; set; }
        // rate → double
        public double ValidSharesPerSecond { get; set; }

        public double NetworkHashrate { get; set; }
        public double NetworkDifficulty { get; set; }

        public DateTime Created { get; set; }
    }

    public class GetPoolStatsResponse
    {
        public AggregatedPoolStats[] Stats { get; set; } = Array.Empty<AggregatedPoolStats>();
    }
}

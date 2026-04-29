using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Ethereum;

public class EthereumWorkerContext : WorkerContextBase
{
    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public override string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public override string Worker { get; set; }

    /// <summary>
    /// Stratum protocol version
    /// </summary>
    public int ProtocolVersion { get; set; }

    /// <summary>
    /// Whether to use Nicehash-style Stratum V2 for this connection
    /// </summary>
    public bool UseNicehashStratumV2 { get; set; } = true;

    /// <summary>
    /// Whether to use MRR-compatible Stratum V2 job params
    /// </summary>
    public bool UseMrrV2Compat { get; set; } = false;

    /// <summary>
    /// Unique value assigned per worker
    /// </summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Current N job(s) assigned to this worker
    /// </summary>
    public Queue<EthereumJob> validJobs { get; private set; } = new();

    public virtual void AddJob(EthereumJob job, int maxActiveJobs)
    {
        if(!validJobs.Contains(job))
            validJobs.Enqueue(job);

        while(validJobs.Count > maxActiveJobs)
            validJobs.Dequeue();
    }

    public EthereumJob GetJob(string jobId)
    {
        // Caller holds lock(context); iterating directly avoids the per-share array
        // allocation that ToArray().FirstOrDefault was doing.
        foreach(var job in validJobs)
        {
            if(job.Id == jobId)
                return job;
        }

        return null;
    }

    public EthereumJob GetJobByHeader(string header)
    {
        // Caller holds lock(context). Used by the V1 (ethproxy) submit path which
        // identifies jobs by header hash rather than job id.
        foreach(var job in validJobs)
        {
            if(job.BlockTemplate.Header.Equals(header))
                return job;
        }

        return null;
    }
}

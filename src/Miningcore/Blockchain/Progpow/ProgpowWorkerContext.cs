using System.Collections.Generic;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowWorkerContext : WorkerContextBase
{
    /// <summary>Usually a wallet address</summary>
    public override string Miner { get; set; }

    /// <summary>Arbitrary worker identififer for miners using multiple rigs</summary>
    public override string Worker { get; set; }

    /// <summary>Unique value assigned per worker</summary>
    public string ExtraNonce1 { get; set; }

    // --- Fast job lookup (O(1)) + FIFO eviction
    private readonly Dictionary<string, ProgpowWorkerJob> jobsById = new();
    private readonly Queue<string> jobOrder = new();

    public virtual void AddJob(ProgpowWorkerJob job, int maxActiveJobs)
    {
        if (!jobsById.ContainsKey(job.Id))
        {
            jobsById[job.Id] = job;
            jobOrder.Enqueue(job.Id);

            while (jobsById.Count > maxActiveJobs && jobOrder.Count > 0)
            {
                var oldest = jobOrder.Dequeue();
                jobsById.Remove(oldest);
            }
        }
    }

    public ProgpowWorkerJob GetJob(string jobId)
    {
        return jobId != null && jobsById.TryGetValue(jobId, out var job) ? job : null;
    }

    // --- Per-worker encoded-target cache (recompute only when Difficulty changes)
    private double lastTargetDiff = double.NaN;
    private string lastEncodedTarget;

    /// <summary>
    /// Returns the encoded target for the current Difficulty, computing it only when Difficulty changed.
    /// Pass the encoder delegate that converts a double difficulty into an encoded target string.
    /// </summary>
    public string GetOrUpdateEncodedTarget(Func<double, string> encoder)
    {
        // Difficulty is a double; exact equality is safe here because we set it ourselves.
        if (lastEncodedTarget == null || Difficulty != lastTargetDiff)
        {
            lastEncodedTarget = encoder(Difficulty);
            lastTargetDiff = Difficulty;
        }
        return lastEncodedTarget;
    }
}
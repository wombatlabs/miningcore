// Equihash/EquihashWorkerContext.cs
using System.Collections.Generic;
using System.Threading;
using Miningcore.Mining;

namespace Miningcore.Blockchain.Equihash;

public class EquihashWorkerContext : WorkerContextBase
{
    /// <summary>Usually a wallet address.</summary>
    public override string Miner { get; set; }

    /// <summary>Arbitrary worker identifier for miners using multiple rigs.</summary>
    public override string Worker { get; set; }

    /// <summary>Unique value assigned per worker.</summary>
    public string ExtraNonce1 { get; set; }

    /// <summary>
    /// Max number of active jobs we keep around for this worker (FIFO window).
    /// Tune this if your miners are frequently submitting on older jobs.
    /// </summary>
    public int MaxActiveJobs { get; set; } = 8;

    // O(1) lookup + FIFO eviction. We keep order in 'jobIds' and random access in 'jobsById'.
    private readonly Queue<string> jobIds = new();
    private readonly Dictionary<string, EquihashJob> jobsById = new();
    private readonly object gate = new object(); // simple synchronization; context can be touched by multiple threads

    /// <summary>
    /// Add a job into the sliding window. If we exceed 'MaxActiveJobs', evict the oldest (FIFO).
    /// </summary>
    public void AddJob(EquihashJob job)
    {
        if(job == null || string.IsNullOrEmpty(job.JobId))
            return;

        lock(gate)
        {
            // Insert only once; update reference if the same id is re-announced
            if(!jobsById.ContainsKey(job.JobId))
            {
                jobsById[job.JobId] = job;
                jobIds.Enqueue(job.JobId);

                // Evict oldest if needed (preserve FIFO semantics)
                while(jobIds.Count > MaxActiveJobs)
                {
                    var oldId = jobIds.Dequeue();
                    jobsById.Remove(oldId);
                }
            }
            else
            {
                // Refresh the job object for the same id (rare, but safe)
                jobsById[job.JobId] = job;
            }
        }
    }

    public void AddJob(EquihashJob job, int maxActiveJobs)
    {
        MaxActiveJobs = maxActiveJobs;
        AddJob(job);
    }

    /// <summary>
    /// O(1) lookup by JobId. Returns null if not found or expired.
    /// </summary>
    public EquihashJob GetJob(string jobId)
    {
        if(string.IsNullOrEmpty(jobId))
            return null;

        lock(gate)
        {
            return jobsById.TryGetValue(jobId, out var job) ? job : null;
        }
    }

    /// <summary>
    /// Enumerate current valid jobs in FIFO order (newest last). Cheap and allocation-free.
    /// </summary>
    public IEnumerable<EquihashJob> EnumerateValidJobs()
    {
        lock(gate)
        {
            foreach(var id in jobIds)
                if(jobsById.TryGetValue(id, out var j))
                    yield return j;
        }
    }

    /// <summary>Clear all tracked jobs (e.g., on new block/connection reset).</summary>
    public void ClearJobs()
    {
        lock(gate)
        {
            jobIds.Clear();
            jobsById.Clear();
        }
    }
}

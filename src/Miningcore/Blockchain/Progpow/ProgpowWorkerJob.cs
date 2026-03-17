using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Miningcore.Stratum;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowWorkerJob
{
    public ProgpowWorkerJob(string jobId, string extraNonce1)
    {
        Id = jobId;
        ExtraNonce1 = extraNonce1;
    }

    public string Id { get; }
    public ProgpowJob Job { get; set; }
    public uint Height { get; set; }
    public string ExtraNonce1 { get; set; }
    public string Bits { get; set; }
    public string SeedHash { get; set; }

    private readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);

    private bool RegisterSubmit(string nonce, string headerHash, string mixHash)
    {
        var key = string.Concat(nonce, headerHash, mixHash); // already lowercased by caller
        return submissions.TryAdd(key, true);
    }


    public (Share Share, string BlockHex) ProcessShare(ILogger logger, StratumConnection worker, string nonce, string headerHash, string mixHash)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<ProgpowWorkerContext>();

        // Validate hex sizes (strict lengths to avoid garbage)
        if(mixHash is null || mixHash.Length != 64)
            throw new StratumException(StratumError.Other, $"incorrect size of mixHash: {mixHash}");

        if(nonce is null || nonce.Length != 16)
            throw new StratumException(StratumError.Other, $"incorrect size of nonce: {nonce}");

        // Do NOT enforce nonce prefix == ExtraNonce1[0..4]: not all KawPoW miners follow that.
        // Just ensure dedupe is case-insensitive.
        if(!RegisterSubmit(nonce.ToLowerInvariant(), headerHash.ToLowerInvariant(), mixHash.ToLowerInvariant()))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        var nonceLong = ulong.Parse(nonce, NumberStyles.HexNumber);

        return Job.ProcessShareInternal(logger, worker, nonceLong, headerHash, mixHash);
    }

}
using System;
using System.Numerics;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Custom.Spectre;

public class SpectreJob : KaspaJob
{
    protected AstroBWTv3 astroBWTv3Hasher;

    public SpectreJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
        this.astroBWTv3Hasher = new AstroBWTv3();
    }

    protected override void SerializeCoinbase(ReadOnlySpan<byte> prePowHash, long timestamp, ulong nonce, Span<byte> result)
    {
        using var stream = new MemoryStream();
        stream.Write(prePowHash);
        stream.Write(BitConverter.GetBytes((ulong)timestamp));
        stream.Write(new byte[32]); // 32 zeros
        stream.Write(BitConverter.GetBytes(nonce));

        var streamBytes = (Span<byte>)stream.ToArray();
        streamBytes.CopyTo(result); // Spectre uses raw before coinbaseHasher
    }

    protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
    {
        var context = worker.ContextAs<KaspaWorkerContext>();
        BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

        Span<byte> coinbaseRaw = stackalloc byte[SpectreConstants.CoinbaseSize];
        SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce, coinbaseRaw);

        Span<byte> coinbase32 = stackalloc byte[32];
        coinbaseHasher.Digest(coinbaseRaw, coinbase32);

        Span<byte> abwt32 = stackalloc byte[32];
        astroBWTv3Hasher.Digest(coinbase32, abwt32);

        Span<byte> mixed32 = stackalloc byte[32];
        ComputeCoinbase(coinbaseRaw, abwt32, mixed32);

        Span<byte> shareHash32 = stackalloc byte[32];
        shareHasher.Digest(mixed32, shareHash32);

        var targetShare = new Target(new BigInteger(shareHash32.ToNewReverseArray(), true, true));
        var shareValue = targetShare.ToUInt256();

        var shareDiff = (double) new BigRational(KaspaConstants.Diff1Target, targetShare.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        bool isBlockCandidate = shareValue <= blockTargetValue;

        if (!isBlockCandidate && ratio < 0.99)
        {
            if (context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;
                if (ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
                stratumDifficulty = context.PreviousDifficulty.Value;
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = (long)BlockTemplate.Header.DaaScore,
            NetworkDifficulty = Difficulty,
            Difficulty = context.Difficulty / shareMultiplier
        };

        if (isBlockCandidate)
        {
            Span<byte> hdrHash = stackalloc byte[32];
            SerializeHeader(BlockTemplate.Header, hdrHash, false);
            result.IsBlockCandidate = true;
            result.BlockHash = hdrHash.ToHexString();
        }

        return result;
    }


    public override void Init(kaspad.RpcBlock blockTemplate, string jobId, double shareMultiplier)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));
        Contract.Requires<ArgumentException>(shareMultiplier > 0);

        JobId = jobId;
        this.shareMultiplier = shareMultiplier;

        var target = new Target(KaspaUtils.CompactToBig(blockTemplate.Header.Bits));
        Difficulty = KaspaUtils.TargetToDifficulty(target.ToBigInteger()) * (double)SpectreConstants.MinHash;
        blockTargetValue = target.ToUInt256();
        BlockTemplate = blockTemplate;

        prePowHashBytes = SerializeHeader(blockTemplate.Header, true);

        var (largeJob, regularJob) = SerializeJobParamsData(prePowHashBytes);
        jobParams = new object[]
        {
        JobId,
        largeJob + BitConverter.GetBytes(blockTemplate.Header.Timestamp).ToHexString().PadLeft(16, '0'),
        regularJob,
        blockTemplate.Header.Timestamp,
        };
    }

}
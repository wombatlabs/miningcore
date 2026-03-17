using System;
using System.Numerics;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;

namespace Miningcore.Blockchain.Kaspa.Custom.Astrix;

public class AstrixJob : KaspaJob
{
    protected Blake3 blake3Hasher;
    protected Sha3_256 sha3_256Hasher;

    public AstrixJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
        this.blake3Hasher = new Blake3();
        this.sha3_256Hasher = new Sha3_256();
    }

    protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
    {
        var context = worker.ContextAs<KaspaWorkerContext>();
        BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

        Span<byte> coinbase32 = stackalloc byte[32];
        SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce, coinbase32);

        Span<byte> blake3_32 = stackalloc byte[32];
        blake3Hasher.Digest(coinbase32, blake3_32);

        Span<byte> sha3_32 = stackalloc byte[32];
        sha3_256Hasher.Digest(blake3_32, sha3_32);

        Span<byte> mixed32 = stackalloc byte[32];
        ComputeCoinbase(prePowHashBytes, sha3_32, mixed32);

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

}
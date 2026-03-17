using System;
using System.Numerics;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;

namespace Miningcore.Blockchain.Kaspa.Custom.Karlsencoin
{
    public class KarlsencoinJob : KaspaJob
    {
        public KarlsencoinJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
            : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
        {
        }

        /// <summary>
        /// Coinbase format for Karlsencoin:
        /// prePowHash || timestamp(uint64) || 32 zero bytes padding || nonce(uint64).
        /// If share hasher is FishHashKarlsen we skip hashing the coinbase (raw is used).
        /// </summary>
        protected override void SerializeCoinbase(ReadOnlySpan<byte> prePowHash, long timestamp, ulong nonce, Span<byte> result)
        {
            using var stream = new MemoryStream();
            stream.Write(prePowHash);
            stream.Write(BitConverter.GetBytes((ulong) timestamp));
            stream.Write(new byte[32]); // 32 zero bytes padding
            stream.Write(BitConverter.GetBytes(nonce));

            var streamBytes = (Span<byte>) stream.ToArray();

            if(shareHasher is not FishHashKarlsen)
                coinbaseHasher.Digest(streamBytes, result);
            else
                streamBytes.CopyTo(result);
        }

        /// <summary>
        /// Validates share, computes difficulty, block-candidate and fills Share.
        /// </summary>
        protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
        {
            var context = worker.ContextAs<KaspaWorkerContext>();
            BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

            // Build coinbase buffer (size depends on hasher type)
            Span<byte> coinbaseBuf = stackalloc byte[(shareHasher is not FishHashKarlsen) ? 32 : KarlsencoinConstants.CoinbaseSize];
            SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce, coinbaseBuf);

            // Hash share
            Span<byte> shareHash32 = stackalloc byte[32];
            if(shareHasher is not FishHashKarlsen)
            {
                Span<byte> mixed32 = stackalloc byte[32];
                ComputeCoinbase(prePowHashBytes, coinbaseBuf, mixed32);
                shareHasher.Digest(mixed32, shareHash32);
            }
            else
            {
                shareHasher.Digest(coinbaseBuf, shareHash32);
            }

            // Convert hash to target/value
            var targetShare = new Target(new BigInteger(shareHash32.ToNewReverseArray(), true, true));
            var shareValue = targetShare.ToUInt256();

            // Difficulty calc (Diff1, not Diff1b). KarlsencoinConstants.Diff1 does NOT exist; use KaspaConstants.Diff1.
            var shareDiff = (double) new BigRational(KaspaConstants.Diff1Target, targetShare.ToBigInteger()) * shareMultiplier;

            // Start with current diff; may fallback to previous if vardiff just changed.
            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;

            var isBlockCandidate = shareValue <= blockTargetValue;

            if(!isBlockCandidate && ratio < 0.99)
            {
                // Allow the previous difficulty shortly after a vardiff retarget
                if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    ratio = shareDiff / context.PreviousDifficulty.Value;
                    if(ratio < 0.99)
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                    // Use previous difficulty for the accepted share
                    stratumDifficulty = context.PreviousDifficulty.Value;
                }
                else
                {
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
                }
            }

            var result = new Share
            {
                BlockHeight = (long) BlockTemplate.Header.DaaScore,
                NetworkDifficulty = Difficulty,
                // Report the difficulty actually used to validate this share
                Difficulty = stratumDifficulty / shareMultiplier
            };

            if(isBlockCandidate)
            {
                result.IsBlockCandidate = true;
                result.BlockHash = shareValue.ToString();
                result.TransactionConfirmationData = shareHash32.ToHexString();
            }

            return result;
        }
    }
}

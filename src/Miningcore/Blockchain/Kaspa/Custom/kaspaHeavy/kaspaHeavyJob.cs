using System;
using System.Numerics;
using System.IO;
using System.Text;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa.Custom.KaspaHeavy
{
    /// <summary>
    /// KASPA vanilla: PoW = HeavyHash(header_bytes_raw_com_nonce_e_timestamp)
    /// </summary>
    public class KaspaHeavyJob : KaspaJob
    {
        public KaspaHeavyJob(IHashAlgorithm dummyHeaderHasher, IHashAlgorithm heavyHash)
            : base(dummyHeaderHasher, heavyHash, heavyHash)
        {
            // Note: ignore "headerHasher" from base for KAS.
            // We use shareHasher (HeavyHash) directly on the raw header bytes.
        }

        protected override Share ProcessShareInternal(StratumConnection worker, string nonceHex)
        {
            var context = worker.ContextAs<KaspaWorkerContext>();

            // 1) Set nonce in header
            BlockTemplate.Header.Nonce = Convert.ToUInt64(nonceHex, 16);

            // 2) Serialize the raw HEADER (without intermediate hash!)
            byte[] headerRaw = SerializeHeaderRaw(BlockTemplate.Header, includePowFields: true);

            // 3) HeavyHash(headerRaw) → powHash
            Span<byte> powHash = stackalloc byte[32];
            shareHasher.Digest(headerRaw, powHash);

            // 4) Calculate diff/target
            var targetShare = new Target(new System.Numerics.BigInteger(powHash.ToNewReverseArray(), true, true));
            var powValue = targetShare.ToUInt256();

            var shareDiff = (double) new BigRational(KaspaConstants.Diff1Target, targetShare.ToBigInteger()) * shareMultiplier;

            var stratumDifficulty = context.Difficulty;
            var ratio = shareDiff / stratumDifficulty;

            bool isBlockCandidate = powValue <= blockTargetValue;

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
                Difficulty = context.Difficulty / shareMultiplier,
                IsBlockCandidate = isBlockCandidate
            };

            if (isBlockCandidate)
            {
                // For logging: you can keep HeavyHash(headerRaw) or the header hash as you like.
                // We maintain the same pattern as the rest of the project:
                result.BlockHash = powHash.ToHexString();
            }

            return result;
        }

        /// <summary>
        /// Serialize KASPA header to raw bytes *without hashing*.
        /// This MUST be byte-compatible with what the node expects for PoW.
        /// </summary>
        private static byte[] SerializeHeaderRaw(kaspad.RpcBlockHeader header, bool includePowFields)
        {
            // includePowFields = true → uses real timestamp and nonce (not zero)
            ulong nonce     = includePowFields ? header.Nonce     : 0UL;
            long  timestamp = includePowFields ? header.Timestamp : 0L;

            using var stream = new MemoryStream();

            // version (u16)
            var versionBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes((ushort)header.Version).ReverseInPlace()
                : BitConverter.GetBytes((ushort)header.Version);
            stream.Write(versionBytes);

            // parents count (u64)
            var parentsBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes((ulong)header.Parents.Count).ReverseInPlace()
                : BitConverter.GetBytes((ulong)header.Parents.Count);
            stream.Write(parentsBytes);

            // parents groups
            foreach (var parent in header.Parents)
            {
                var parentCountBytes = (!BitConverter.IsLittleEndian)
                    ? BitConverter.GetBytes((ulong)parent.ParentHashes.Count).ReverseInPlace()
                    : BitConverter.GetBytes((ulong)parent.ParentHashes.Count);
                stream.Write(parentCountBytes);

                foreach (var parentHash in parent.ParentHashes)
                    stream.Write(parentHash.HexToByteArray());
            }

            // fixed roots/commitments
            stream.Write(header.HashMerkleRoot.HexToByteArray());
            stream.Write(header.AcceptedIdMerkleRoot.HexToByteArray());
            stream.Write(header.UtxoCommitment.HexToByteArray());

            // timestamp (u64), bits (u32), nonce (u64)
            var timestampBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes((ulong)timestamp).ReverseInPlace()
                : BitConverter.GetBytes((ulong)timestamp);
            stream.Write(timestampBytes);

            var bitsBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes(header.Bits).ReverseInPlace()
                : BitConverter.GetBytes(header.Bits);
            stream.Write(bitsBytes);

            var nonceBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes(nonce).ReverseInPlace()
                : BitConverter.GetBytes(nonce);
            stream.Write(nonceBytes);

            // DAA score (u64), blue score (u64)
            var daaScoreBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes(header.DaaScore).ReverseInPlace()
                : BitConverter.GetBytes(header.DaaScore);
            stream.Write(daaScoreBytes);

            var blueScoreBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes(header.BlueScore).ReverseInPlace()
                : BitConverter.GetBytes(header.BlueScore);
            stream.Write(blueScoreBytes);

            // blue work (len u64 + bytes) — attention to padding
            var blueWork = header.BlueWork.PadLeft(header.BlueWork.Length + (header.BlueWork.Length % 2), '0');
            var blueWorkBytes = blueWork.HexToByteArray();

            var blueWorkLengthBytes = (!BitConverter.IsLittleEndian)
                ? BitConverter.GetBytes((ulong)blueWorkBytes.Length).ReverseInPlace()
                : BitConverter.GetBytes((ulong)blueWorkBytes.Length);
            stream.Write(blueWorkLengthBytes);
            stream.Write(blueWorkBytes);

            // pruning point (32 bytes)
            stream.Write(header.PruningPoint.HexToByteArray());

            return stream.ToArray();
        }
    }
}

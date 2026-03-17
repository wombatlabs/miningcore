using System;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Collections.Concurrent;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa
{
    public class KaspaJob
    {
        protected double shareMultiplier;

        public kaspad.RpcBlock BlockTemplate { get; protected set; }
        public double Difficulty { get; protected set; }
        public string JobId { get; protected set; }
        public uint256 blockTargetValue { get; protected set; }

        protected object[] jobParams;
        protected byte[] prePowHashBytes;

        private readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);

        protected IHashAlgorithm blockHeaderHasher;
        protected IHashAlgorithm coinbaseHasher;
        protected IHashAlgorithm shareHasher;

        public KaspaJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher)
        {
            Contract.RequiresNonNull(customBlockHeaderHasher);
            Contract.RequiresNonNull(customCoinbaseHasher);
            Contract.RequiresNonNull(customShareHasher);

            blockHeaderHasher = customBlockHeaderHasher;
            coinbaseHasher = customCoinbaseHasher;
            shareHasher = customShareHasher;
        }

        public object[] GetJobParams() => jobParams;

        protected bool RegisterSubmit(string key) => submissions.TryAdd(key, true);

        private static string BuildFinalNonceHex(string extraNonce1, string submitted, int totalLen = KaspaConstants.NonceLength)
        {
            var raw = (extraNonce1 ?? string.Empty) + (submitted ?? string.Empty);
            if (raw.Length > totalLen)
                raw = raw[^totalLen..];
            return raw.PadLeft(totalLen, '0');
        }

        #region Matrix/generator (para altcoins)

        protected virtual ushort[][] GenerateMatrix(ReadOnlySpan<byte> prePowHash)
        {
            ushort[][] matrix = new ushort[64][];
            for (int i = 0; i < 64; i++)
                matrix[i] = new ushort[64];

            var generator = new KaspaXoShiRo256PlusPlus(prePowHash);
            while (true)
            {
                for (int i = 0; i < 64; i++)
                {
                    for (int j = 0; j < 64; j += 16)
                    {
                        ulong val = generator.Uint64();
                        for (int shift = 0; shift < 16; shift++)
                            matrix[i][j + shift] = (ushort)((val >> (4 * shift)) & 0x0F);
                    }
                }
                if (ComputeRank(matrix) == 64)
                    return matrix;
            }
        }

        protected virtual int ComputeRank(ushort[][] matrix)
        {
            const double Eps = 1e-9;
            double[][] B = matrix.Select(row => row.Select(val => (double)val).ToArray()).ToArray();
            int rank = 0;
            bool[] rowSelected = new bool[64];

            for (int i = 0; i < 64; i++)
            {
                int j;
                for (j = 0; j < 64; j++)
                {
                    if (!rowSelected[j] && Math.Abs(B[j][i]) > Eps)
                        break;
                }
                if (j != 64)
                {
                    rank++;
                    rowSelected[j] = true;
                    double pivot = B[j][i];

                    for (int p = i + 1; p < 64; p++)
                        B[j][p] /= pivot;

                    for (int k = 0; k < 64; k++)
                    {
                        if (k != j && Math.Abs(B[k][i]) > Eps)
                        {
                            for (int p = i + 1; p < 64; p++)
                                B[k][p] -= B[j][p] * B[k][i];
                        }
                    }
                }
            }

            return rank;
        }

        #endregion

        #region Serializações

        protected virtual void SerializeCoinbase(ReadOnlySpan<byte> prePowHash, long timestamp, ulong nonce, Span<byte> dest)
        {
            using var stream = new System.IO.MemoryStream();
            stream.Write(prePowHash);
            stream.Write(BitConverter.GetBytes((ulong)timestamp));
            stream.Write(new byte[32]); // padding 32 zeros
            stream.Write(BitConverter.GetBytes(nonce));
            coinbaseHasher.Digest(stream.ToArray(), dest);
        }

        protected virtual void SerializeHeader(kaspad.RpcBlockHeader header, Span<byte> dest, bool isPrePow = true)
        {
            ulong nonce = isPrePow ? 0 : header.Nonce;
            long timestamp = isPrePow ? 0 : header.Timestamp;

            using var stream = new System.IO.MemoryStream();

            var versionBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes((ushort)header.Version).ReverseInPlace() : BitConverter.GetBytes((ushort)header.Version);
            stream.Write(versionBytes);

            var parentsBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes((ulong)header.Parents.Count).ReverseInPlace() : BitConverter.GetBytes((ulong)header.Parents.Count);
            stream.Write(parentsBytes);

            foreach (var parent in header.Parents)
            {
                var cntBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes((ulong)parent.ParentHashes.Count).ReverseInPlace() : BitConverter.GetBytes((ulong)parent.ParentHashes.Count);
                stream.Write(cntBytes);
                foreach (var ph in parent.ParentHashes)
                    stream.Write(ph.HexToByteArray());
            }

            stream.Write(header.HashMerkleRoot.HexToByteArray());
            stream.Write(header.AcceptedIdMerkleRoot.HexToByteArray());
            stream.Write(header.UtxoCommitment.HexToByteArray());

            var timestampBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes((ulong)timestamp).ReverseInPlace() : BitConverter.GetBytes((ulong)timestamp);
            var bitsBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes(header.Bits).ReverseInPlace() : BitConverter.GetBytes(header.Bits);
            var nonceBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes(nonce).ReverseInPlace() : BitConverter.GetBytes(nonce);
            var daaScoreBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes(header.DaaScore).ReverseInPlace() : BitConverter.GetBytes(header.DaaScore);
            var blueScoreBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes(header.BlueScore).ReverseInPlace() : BitConverter.GetBytes(header.BlueScore);

            stream.Write(timestampBytes);
            stream.Write(bitsBytes);
            stream.Write(nonceBytes);
            stream.Write(daaScoreBytes);
            stream.Write(blueScoreBytes);

            var blueWork = header.BlueWork.PadLeft(header.BlueWork.Length + (header.BlueWork.Length % 2), '0');
            var blueWorkBytes = blueWork.HexToByteArray();
            var blueWorkLengthBytes = (!BitConverter.IsLittleEndian) ? BitConverter.GetBytes((ulong)blueWorkBytes.Length).ReverseInPlace() : BitConverter.GetBytes((ulong)blueWorkBytes.Length);
            stream.Write(blueWorkLengthBytes);
            stream.Write(blueWorkBytes);

            stream.Write(header.PruningPoint.HexToByteArray());

            Span<byte> hash32 = stackalloc byte[32];
            blockHeaderHasher.Digest(stream.ToArray(), hash32);
            hash32.CopyTo(dest);
        }

        protected byte[] SerializeHeader(kaspad.RpcBlockHeader header, bool isPrePow = true)
        {
            var buf = new byte[32];
            SerializeHeader(header, buf, isPrePow);
            return buf;
        }

        protected virtual void ComputeCoinbase(ReadOnlySpan<byte> prePowHash, ReadOnlySpan<byte> data, Span<byte> dest)
        {
            var matrix = GenerateMatrix(prePowHash);
            Span<ushort> vector = stackalloc ushort[64];
            Span<ushort> product = stackalloc ushort[64];

            for (int i = 0; i < 32; i++)
            {
                vector[2 * i] = (ushort)(data[i] >> 4);
                vector[2 * i + 1] = (ushort)(data[i] & 0x0F);
            }

            for (int i = 0; i < 64; i++)
            {
                int sum = 0;
                for (int j = 0; j < 64; j++)
                    sum += matrix[i][j] * vector[j];

                product[i] = (ushort)(sum >> 10);
            }

            for (int i = 0; i < 32; i++)
            {
                byte mix = (byte)((product[2 * i] << 4) | product[2 * i + 1]);
                dest[i] = (byte)(data[i] ^ mix);
            }
        }

        #endregion

        protected virtual Share ProcessShareInternal(StratumConnection worker, string nonce)
        {
            var context = worker.ContextAs<KaspaWorkerContext>();

            BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

            Span<byte> coinbase32 = stackalloc byte[32];
            SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce, coinbase32);

            Span<byte> mixed32 = stackalloc byte[32];
            ComputeCoinbase(prePowHashBytes, coinbase32, mixed32);

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

        public virtual Share ProcessShare(StratumConnection worker, string nonce)
        {
            Contract.RequiresNonNull(worker);
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

            var context = worker.ContextAs<KaspaWorkerContext>();

            if (nonce.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                nonce = nonce[2..];

            nonce = BuildFinalNonceHex(context.ExtraNonce1, nonce, KaspaConstants.NonceLength);

            if (!RegisterSubmit($"{JobId}:{nonce}"))
                throw new StratumException(StratumError.DuplicateShare, "duplicate share");

            return ProcessShareInternal(worker, nonce);
        }

        public virtual void Init(kaspad.RpcBlock blockTemplate, string jobId, double shareMultiplier)
        {
            Contract.RequiresNonNull(blockTemplate);
            Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));
            Contract.Requires<ArgumentException>(shareMultiplier > 0);

            JobId = jobId;
            this.shareMultiplier = shareMultiplier;

            var target = new Target(KaspaUtils.CompactToBig(blockTemplate.Header.Bits));
            Difficulty = KaspaUtils.TargetToDifficulty(target.ToBigInteger()) * (double)KaspaConstants.MinHash;
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

        protected virtual (string, ulong[]) SerializeJobParamsData(ReadOnlySpan<byte> prePowHash)
        {
            ulong[] preHashU64s = new ulong[4];
            var sb = new StringBuilder(64);

            for (int i = 0; i < 4; i++)
            {
                var slice = prePowHash.Slice(i * 8, 8).ToArray();
                sb.Append(slice.ToHexString().PadLeft(16, '0'));

                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(slice);

                preHashU64s[i] = BitConverter.ToUInt64(slice, 0);
            }

            return (sb.ToString(), preHashU64s);
        }
    }

    public class KaspaXoShiRo256PlusPlus
    {
        private readonly ulong[] s = new ulong[4];

        public KaspaXoShiRo256PlusPlus(ReadOnlySpan<byte> prePowHash)
        {
            Contract.Requires<ArgumentException>(prePowHash.Length >= 32);
            for (int i = 0; i < 4; i++)
            {
                var slice = prePowHash.Slice(i * 8, 8).ToArray();
                if (!BitConverter.IsLittleEndian)
                    Array.Reverse(slice);
                s[i] = BitConverter.ToUInt64(slice, 0);
            }
        }

        public ulong Uint64()
        {
            ulong result = RotateLeft64(s[0] + s[3], 23) + s[0];
            ulong t = s[1] << 17;
            s[2] ^= s[0];
            s[3] ^= s[1];
            s[1] ^= s[2];
            s[0] ^= s[3];
            s[2] ^= t;
            s[3] = RotateLeft64(s[3], 45);
            return result;
        }

        private static ulong RotateLeft64(ulong value, int offset) =>
            (value << offset) | (value >> (64 - offset));
    }
}

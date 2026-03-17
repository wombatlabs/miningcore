using System.Text;
using System.Numerics;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Util;
using NBitcoin;

namespace Miningcore.Blockchain.Kaspa.Custom.WagLayla;

public class WagLaylaJob : KaspaJob
{
    protected Blake3 blake3Hasher;
    protected Sha3_256 sha3_256Hasher;

    public WagLaylaJob(IHashAlgorithm customBlockHeaderHasher, IHashAlgorithm customCoinbaseHasher, IHashAlgorithm customShareHasher) 
        : base(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher)
    {
        this.blake3Hasher = new Blake3();
        this.sha3_256Hasher = new Sha3_256();
    }

    protected override void ComputeCoinbase(ReadOnlySpan<byte> prePowHash, ReadOnlySpan<byte> data, Span<byte> result)
    {
        ushort[][] matrix = GenerateMatrix(prePowHash);
        data.CopyTo(result); // Create a copy to work with

        // Convert bytes to nibbles
        ushort[] v = new ushort[64];
        for (int i = 0; i < 16; i++)
        {
            v[i * 4] = (ushort)(data[i * 2] >> 4);
            v[i * 4 + 1] = (ushort)(data[i * 2] & 0x0F);
            v[i * 4 + 2] = (ushort)(data[i * 2 + 1] >> 4);
            v[i * 4 + 3] = (ushort)(data[i * 2 + 1] & 0x0F);
        }

        // Perform matrix multiplication with XOR folding
        for (int i = 0; i < 16; i++)
        {
            ushort sum1 = 0, sum2 = 0, sum3 = 0, sum4 = 0;

            for (int j = 0; j < 64; j++)
            {
                sum1 += (ushort)(matrix[4 * i][j] * v[j]);
                sum2 += (ushort)(matrix[4 * i + 1][j] * v[j]);
                sum3 += (ushort)(matrix[4 * i + 2][j] * v[j]);
                sum4 += (ushort)(matrix[4 * i + 3][j] * v[j]);
            }

            // XOR folding of sums
            sum1 = (ushort)((sum1 & 0xF) ^ ((sum1 >> 4) & 0xF) ^ ((sum1 >> 8) & 0xF));
            sum2 = (ushort)((sum2 & 0xF) ^ ((sum2 >> 4) & 0xF) ^ ((sum2 >> 8) & 0xF));
            sum3 = (ushort)((sum3 & 0xF) ^ ((sum3 >> 4) & 0xF) ^ ((sum3 >> 8) & 0xF));
            sum4 = (ushort)((sum4 & 0xF) ^ ((sum4 >> 4) & 0xF) ^ ((sum4 >> 8) & 0xF));

            // XOR with original data
            result[i * 2] ^= (byte)(((byte)sum1 << 4) | (byte)sum2);
            result[i * 2 + 1] ^= (byte)(((byte)sum3 << 4) | (byte)sum4);
        }
    }

    protected override Share ProcessShareInternal(StratumConnection worker, string nonce)
    {
        var context = worker.ContextAs<KaspaWorkerContext>();

        BlockTemplate.Header.Nonce = Convert.ToUInt64(nonce, 16);

        Span<byte> coinbaseBytes = stackalloc byte[32];
        SerializeCoinbase(prePowHashBytes, BlockTemplate.Header.Timestamp, BlockTemplate.Header.Nonce, coinbaseBytes);

        Span<byte> sha3_256Bytes = stackalloc byte[32];
        sha3_256Hasher.Digest(coinbaseBytes, sha3_256Bytes);

        Span<byte> matrixBytes = stackalloc byte[32];
        ComputeCoinbase(prePowHashBytes, sha3_256Bytes, matrixBytes);

        Span<byte> hashCoinbaseBytes = stackalloc byte[32];
        shareHasher.Digest(matrixBytes, hashCoinbaseBytes);

        var targetHashCoinbaseBytes = new Target(new BigInteger(hashCoinbaseBytes.ToNewReverseArray(), true, true));
        var hashCoinbaseBytesValue = targetHashCoinbaseBytes.ToUInt256();

        // Difficulty calc (usa Diff1Target da Kaspa)
        var shareDiff = (double) new BigRational(KaspaConstants.Diff1Target, targetHashCoinbaseBytes.ToBigInteger()) * shareMultiplier;

        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // Block-candidate?
        var isBlockCandidate = hashCoinbaseBytesValue <= blockTargetValue;

        // Testa contra a diff atual (ou a anterior, se houve retarget muito recente)
        if(!isBlockCandidate && ratio < 0.99)
        {
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                stratumDifficulty = context.PreviousDifficulty.Value;
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = (long) BlockTemplate.Header.DaaScore,
            NetworkDifficulty = Difficulty,
            Difficulty = context.Difficulty / shareMultiplier
        };

        if(isBlockCandidate)
        {
            Span<byte> hashBytes = stackalloc byte[32];
            SerializeHeader(BlockTemplate.Header, hashBytes, false);
            result.IsBlockCandidate = true;
            result.BlockHash = hashBytes.ToHexString();
        }

        return result;
    }

}
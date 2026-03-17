using System;
using System.Globalization;
using System.Text;
using System.Buffers;
using System.Runtime.CompilerServices;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowJobParams
{
    public ulong Height { get; init; }
    public bool CleanJobs { get; set; }
}

public class ProgpowJob : BitcoinJob
{
    protected IProgpowCache progpowHasher;
    private new ProgpowJobParams jobParams;

    // ===== Exact fixed-point config (no BigRational in hot path) =====
    private static readonly System.Numerics.BigInteger SCALE = new System.Numerics.BigInteger(1_000_000_000_000L); // 1e12
    private const long SCALE_M = 1_000_000; // 1e6 for shareMultiplier
    private long shareMultScaled; // set in Init()

    protected virtual byte[] SerializeHeader(Span<byte> coinbaseHash)
    {
        // Build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version
        var version = BlockTemplate.Version;

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(BlockTemplate.CurTime),
            Nonce = BlockTemplate.Height
        };

        return blockHeader.ToBytes();
    }

    public virtual (Share Share, string BlockHex) ProcessShareInternal(ILogger logger,
        StratumConnection worker, ulong nonce, string inputHeaderHash, string mixHash)
    {
        var context = worker.ContextAs<ProgpowWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // 1) Build coinbase and hash it
        var coinbase = SerializeCoinbase(extraNonce1);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // 2) Build & hash block header (header-hash is big-endian hex string to miners)
        var headerBytes = SerializeHeader(coinbaseHash);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash);
        headerHash.Reverse(); // convert to BE for hex string compare

        var headerHashHex = headerHash.ToHexString();
        if(!headerHashHex.Equals(inputHeaderHash, StringComparison.OrdinalIgnoreCase))
            throw new StratumException(StratumError.MinusOne, "bad header-hash");

        // 3) KawPoW hash (native) using height/epoch cache
        byte[] headerArr = ArrayPool<byte>.Shared.Rent(32);
        try
        {
            headerHash.CopyTo(headerArr);
            if(!progpowHasher.Compute(logger, (int) BlockTemplate.Height, headerArr, nonce, out var mixHashOut, out var resultBytes))
                throw new StratumException(StratumError.MinusOne, "bad hash");

            // Be tolerant to miner hex casing
            var mixHex = mixHashOut.ToHexString();
            if(!mixHex.Equals(mixHash, StringComparison.OrdinalIgnoreCase))
                throw new StratumException(StratumError.MinusOne, "bad mix-hash");

            // Convert to pool-internal endian for further math
            resultBytes.ReverseInPlace();
            mixHashOut.ReverseInPlace();

            var resultValue = new uint256(resultBytes);
            var resultValueBig = resultBytes.AsSpan().ToBigInteger();

            // Variant Diff1 (exact)
            var diff1ForVariantBig = (coin.Symbol == "FIRO"
                ? FiroConstants.Diff1
                : RavencoinConstants.Diff1);

            // ---- EXACT MATH: shareDiff in fixed-point (scaled by 1e12) ----
            var shareDiffScaled = ComputeShareDiffScaledExact(diff1ForVariantBig, resultValueBig);
            // Optional: for logs only (double)
            var shareDiff = (double) shareDiffScaled / 1_000_000_000_000d;
            // ----------------------------------------------------------------

            // Check block candidate with exact uint256 compare (unchanged)
            var isBlockCandidate = resultValue <= blockTargetValue;

            // VarDiff thresholds using exact integer comparisons
            var stratumDifficulty = context.Difficulty;

            if(!isBlockCandidate && !MeetsDifficultyThresholdExact(shareDiffScaled, stratumDifficulty, 0.99))
            {
                if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
                {
                    if(!MeetsDifficultyThresholdExact(shareDiffScaled, context.PreviousDifficulty.Value, 0.99))
                        throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                    stratumDifficulty = context.PreviousDifficulty.Value;
                }
                else
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
            }

            // Compose share (external API stays as before)
            var result = new Share
            {
                BlockHeight = BlockTemplate.Height,
                NetworkDifficulty = Difficulty,
                Difficulty = stratumDifficulty / shareMultiplier,
            };

            if(!isBlockCandidate)
                return (result, null);

            // Block candidate path
            result.IsBlockCandidate = true;
            result.BlockHash = resultBytes.ReverseInPlace().ToHexString();

            var blockHex = SerializeBlock(headerBytes, coinbase, nonce, mixHashOut).ToHexString();
            return (result, blockHex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(headerArr);
        }
    }

    protected virtual byte[] SerializeCoinbase(string extraNonce1)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var len = coinbaseInitial.Length + extraNonce1Bytes.Length + coinbaseFinal.Length;

        var result = new byte[len];
        Buffer.BlockCopy(coinbaseInitial, 0, result, 0, coinbaseInitial.Length);
        Buffer.BlockCopy(extraNonce1Bytes, 0, result, coinbaseInitial.Length, extraNonce1Bytes.Length);
        Buffer.BlockCopy(coinbaseFinal, 0, result, coinbaseInitial.Length + extraNonce1Bytes.Length, coinbaseFinal.Length);

        return result;
    }

    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase, ulong nonce, byte[] mixHash)
    {
        // Precompute size: header(80) + nonce(8) + mix(32) + varint(txcount) + coinbase + txs
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var txCount = (uint) BlockTemplate.Transactions.Length + 1;
        var varIntLen = VarIntSize(txCount); // local helper (compat)

        int total = header.Length + sizeof(ulong) + 32 + varIntLen + coinbase.Length + rawTransactionBuffer.Length;

        byte[] buf = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            var span = buf.AsSpan(0, total);
            int o = 0;

            // header (80 bytes)
            header.CopyTo(span.Slice(o)); o += header.Length;

            // nonce (LE)
            BitConverter.TryWriteBytes(span.Slice(o, 8), nonce); o += 8;

            // mixHash (LE as bytes already)
            mixHash.CopyTo(span.Slice(o)); o += 32;

            // varint txcount
            o += WriteVarInt(span.Slice(o), txCount);

            // coinbase
            coinbase.CopyTo(span.Slice(o)); o += coinbase.Length;

            // other txs
            rawTransactionBuffer.CopyTo(span.Slice(o)); // o += rawTransactionBuffer.Length;

            // Compact to exact array
            var result = new byte[total];
            Buffer.BlockCopy(buf, 0, result, 0, total);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int VarIntSize(ulong value) =>
        value < 0xFD ? 1 :
        (value <= 0xFFFF ? 3 :
        (value <= 0xFFFFFFFF ? 5 : 9));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteVarInt(Span<byte> dst, ulong value)
    {
        // Minimal VarInt writer (NBitcoin compat)
        if(value < 0xFD)
        {
            dst[0] = (byte) value;
            return 1;
        }

        if(value <= 0xFFFF)
        {
            dst[0] = 0xFD;
            dst[1] = (byte) (value & 0xFF);
            dst[2] = (byte) (value >> 8);
            return 3;
        }

        if(value <= 0xFFFFFFFF)
        {
            dst[0] = 0xFE;
            BitConverter.TryWriteBytes(dst.Slice(1, 4), (uint) value);
            return 5;
        }

        dst[0] = 0xFF;
        BitConverter.TryWriteBytes(dst.Slice(1, 8), value);
        return 9;
    }

    // ===== Exact difficulty helpers =====

    /// <summary>
    /// Returns scaled share-diff: floor( (Diff1 * SCALE) / resultValue ) * shareMultiplierScaled / SCALE_M
    /// The returned value is scaled by SCALE (1e12).
    /// </summary>
    private System.Numerics.BigInteger ComputeShareDiffScaledExact(System.Numerics.BigInteger diff1, System.Numerics.BigInteger resultValue)
    {
        if (resultValue.Sign <= 0)
            resultValue = System.Numerics.BigInteger.One; // guard (should never be 0)

        var baseScaled = (diff1 * SCALE) / resultValue;
        var withMult = (baseScaled * shareMultScaled) / SCALE_M;

        if (withMult.Sign <= 0)
            withMult = System.Numerics.BigInteger.One;

        return withMult; // still scaled by SCALE
    }

    /// <summary>
    /// Returns true if shareDiffScaled >= threshold * stratumDiff (all in fixed-point).
    /// threshold is a double like 0.99; converted to fixed-point internally.
    /// </summary>
    private bool MeetsDifficultyThresholdExact(System.Numerics.BigInteger shareDiffScaled, double stratumDifficulty, double threshold)
    {
        const long SCALE_T = 1_000_000; // 1e6 for threshold
        if (!double.IsFinite(stratumDifficulty) || stratumDifficulty <= 0d)
            stratumDifficulty = 1d;

        var thrScaled = (long) Math.Round(threshold * SCALE_T, MidpointRounding.AwayFromZero);
        if (thrScaled <= 0) thrScaled = 1;

        // rightScaled = round(stratumDifficulty * SCALE) * thrScaled / SCALE_T
        decimal d = (decimal) stratumDifficulty;
        var rightScaled = new System.Numerics.BigInteger((long) Math.Round(d * 1_000_000_000_000m, MidpointRounding.AwayFromZero));
        rightScaled = (rightScaled * thrScaled) / SCALE_T;

        return shareDiffScaled >= rightScaled;
    }

    #region API-Surface

    public virtual void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher, IProgpowCache progpowHasher)
    {
        // --- Argument checks
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.RequiresNonNull(progpowHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        // --- Bind coin/template & context (unchanged)
        this.coin = pc.Template.As<ProgpowCoinTemplate>();
        this.txVersion = coin.CoinbaseTxVersion;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        this.BlockTemplate = blockTemplate;
        this.JobId = jobId;

        // --- Coinbase tag (unchanged)
        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString)
            ? cc.PaymentProcessing?.CoinbaseString.Trim()
            : "Miningcore";

        if(!string.IsNullOrEmpty(coinbaseString))
            this.scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        // --- Network difficulty from block template (unchanged)
        this.Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        // --- Select KawPoW profile parameters ONCE per job (fast for the hot path)
        {
            var sel = Miningcore.Blockchain.Progpow.Kawpow
.KawpowVariant.SelectFor(pc.Template);
            this.extraNoncePlaceHolderLength = sel.extranonceLen; // field provided by base class
        }

        // --- Masternode / Payee / Founder / MinerFund (unchanged)
        if(coin.HasMasterNodes)
        {
            masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();

            if(coin.Symbol == "FIRO")
            {
                if(masterNodeParameters.Extra?.ContainsKey("znode") == true)
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["znode"]);
            }

            if(!string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
            {
                txVersion = 3;
                const uint txType = 5;
                txVersion += txType << 16;
            }
        }

        if(coin.HasPayee)
            payeeParameters = BlockTemplate.Extra.SafeExtensionDataAs<PayeeBlockTemplateExtra>();

        if(coin.HasFounderFee)
            founderParameters = BlockTemplate.Extra.SafeExtensionDataAs<FounderBlockTemplateExtra>();

        if(coin.HasMinerFund)
            minerFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerFundTemplateExtra>("coinbasetxn", "minerfund");

        // --- Hashers & target (unchanged)
        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;
        this.progpowHasher = progpowHasher;

        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            this.blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            this.blockTargetValue = tmp.ToUInt256();
        }

        // --- Precompute branches & coinbase (unchanged)
        BuildMerkleBranches();
        BuildCoinbase();

        // --- Notify params cache (unchanged)
        this.jobParams = new ProgpowJobParams
        {
            Height = BlockTemplate.Height,
            CleanJobs = false
        };

        // --- Cache scaled shareMultiplier for exact math ---
        shareMultScaled = (long) Math.Round(shareMultiplier * SCALE_M, MidpointRounding.AwayFromZero);
        if (shareMultScaled <= 0) shareMultScaled = 1;
    }

    public new object GetJobParams(bool isNew)
    {
        jobParams.CleanJobs = isNew;
        return jobParams;
    }

    public void PrepareWorkerJob(ProgpowWorkerJob workerJob, out string headerHash)
    {
        workerJob.Job = this;
        workerJob.Height = BlockTemplate.Height;
        workerJob.Bits = BlockTemplate.Bits;
        workerJob.SeedHash = progpowHasher.SeedHash.ToHexString();
        headerHash = CreateHeaderHash(workerJob);
    }

    private string CreateHeaderHash(ProgpowWorkerJob workerJob)
    {
        var headerHasher = coin.HeaderHasherValue;
        var coinbaseHasher = coin.CoinbaseHasherValue;
        var extraNonce1 = workerJob.ExtraNonce1;

        var coinbase = SerializeCoinbase(extraNonce1);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        var headerBytes = SerializeHeader(coinbaseHash);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash);
        headerHash.Reverse();

        return headerHash.ToHexString();
    }

    #endregion // API-Surface
}

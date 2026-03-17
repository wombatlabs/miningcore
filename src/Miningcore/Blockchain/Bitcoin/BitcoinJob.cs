using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using Contract = Miningcore.Contracts.Contract;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Bitcoin;

public class BitcoinJob
{
    // Logger must be inside the class (file-scoped namespace disallows members before it)
    private static readonly NLog.ILogger logger = NLog.LogManager.GetCurrentClassLogger();

    protected IHashAlgorithm blockHasher;
    protected IMasterClock clock;
    protected IHashAlgorithm coinbaseHasher;
    protected double shareMultiplier;
    protected int extraNoncePlaceHolderLength;
    protected IHashAlgorithm headerHasher;
    protected bool isPoS;
    protected string txComment;
    protected PayeeBlockTemplateExtra payeeParameters;

    protected Network network;
    protected IDestination poolAddressDestination;
    protected BitcoinTemplate coin;
    private BitcoinTemplate.BitcoinNetworkParams networkParams;
    protected readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    protected uint256 blockTargetValue;
    protected byte[] coinbaseFinal;
    protected string coinbaseFinalHex;
    protected byte[] coinbaseInitial;
    protected string coinbaseInitialHex;
    protected string[] merkleBranchesHex;
    protected MerkleTree mt;

    ///////////////////////////////////////////
    // GetJobParams related properties

    protected object[] jobParams;
    protected string previousBlockHashReversedHex;
    protected Money rewardToPool;
    protected Transaction txOut;

    // serialization constants
    protected byte[] scriptSigFinalBytes;

    protected static byte[] sha256Empty = new byte[32];
    protected uint txVersion = 1u; // transaction version (currently 1) - see https://en.bitcoin.it/wiki/Transaction

    protected static uint txInputCount = 1u;
    protected static uint txInPrevOutIndex = (uint) (Math.Pow(2, 32) - 1);
    protected static uint txInSequence;
    protected static uint txLockTime;

    protected virtual void BuildMerkleBranches()
    {
        var transactionHashes = BlockTemplate.Transactions
            .Select(tx => (tx.TxId ?? tx.Hash)
                .HexToByteArray()
                .ReverseInPlace())
            .ToArray();

        mt = new MerkleTree(transactionHashes);

        merkleBranchesHex = mt.Steps
            .Select(x => x.ToHexString())
            .ToArray();
    }

    protected virtual void BuildCoinbase()
    {
        // generate script parts
        var sigScriptInitial = GenerateScriptSigInitial();
        var sigScriptInitialBytes = sigScriptInitial.ToBytes();

        var sigScriptLength = (uint) (
            sigScriptInitial.Length +
            extraNoncePlaceHolderLength +
            scriptSigFinalBytes.Length);

        // output transaction
        txOut = CreateOutputTransaction();

        // build coinbase initial
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // version
            bs.ReadWrite(ref txVersion);

            // timestamp for POS coins
            if(isPoS)
            {
                var timestamp = BlockTemplate.CurTime;
                bs.ReadWrite(ref timestamp);
            }

            // serialize (simulated) input transaction
            bs.ReadWriteAsVarInt(ref txInputCount);
            bs.ReadWrite(sha256Empty);
            bs.ReadWrite(ref txInPrevOutIndex);

            // signature script initial part
            bs.ReadWriteAsVarInt(ref sigScriptLength);
            bs.ReadWrite(sigScriptInitialBytes);

            // done
            coinbaseInitial = stream.ToArray();
            coinbaseInitialHex = coinbaseInitial.ToHexString();
        }

        // build coinbase final
        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // signature script final part
            bs.ReadWrite(scriptSigFinalBytes);

            // tx in sequence
            bs.ReadWrite(ref txInSequence);

            // serialize output transaction
            var txOutBytes = SerializeOutputTransaction(txOut);
            bs.ReadWrite(txOutBytes);

            // misc
            bs.ReadWrite(ref txLockTime);

            // Extension point
            AppendCoinbaseFinal(bs);

            // done
            coinbaseFinal = stream.ToArray();
            coinbaseFinalHex = coinbaseFinal.ToHexString();
        }
    }

    protected virtual void AppendCoinbaseFinal(BitcoinStream bs)
    {
        if(!string.IsNullOrEmpty(txComment))
        {
            var data = Encoding.ASCII.GetBytes(txComment);
            bs.ReadWriteAsVarString(ref data);
        }

        if(coin.HasMasterNodes && !string.IsNullOrEmpty(masterNodeParameters.CoinbasePayload))
        {
            var data = masterNodeParameters.CoinbasePayload.HexToByteArray();
            bs.ReadWriteAsVarString(ref data);
        }
    }

    protected virtual byte[] SerializeOutputTransaction(Transaction tx)
    {
        var withDefaultWitnessCommitment = !string.IsNullOrEmpty(BlockTemplate.DefaultWitnessCommitment);

        var outputCount = (uint) tx.Outputs.Count;
        if(withDefaultWitnessCommitment)
            outputCount++;

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            // write output count
            bs.ReadWriteAsVarInt(ref outputCount);

            long amount;
            byte[] raw;
            uint rawLength;

            // serialize witness (segwit)
            if(withDefaultWitnessCommitment)
            {
                amount = 0;
                raw = BlockTemplate.DefaultWitnessCommitment.HexToByteArray();
                rawLength = (uint) raw.Length;

                bs.ReadWrite(ref amount);
                bs.ReadWriteAsVarInt(ref rawLength);
                bs.ReadWrite(raw);
            }

            // serialize outputs
            foreach(var output in tx.Outputs)
            {
                amount = output.Value.Satoshi;
                var outScript = output.ScriptPubKey;
                raw = outScript.ToBytes(true);
                rawLength = (uint) raw.Length;

                bs.ReadWrite(ref amount);
                bs.ReadWriteAsVarInt(ref rawLength);
                bs.ReadWrite(raw);
            }

            return stream.ToArray();
        }
    }

    protected virtual Script GenerateScriptSigInitial()
    {
        var now = ((DateTimeOffset) clock.Now).ToUnixTimeSeconds();

        // script ops
        var ops = new List<Op>();

        // push block height
        ops.Add(Op.GetPushOp(BlockTemplate.Height));

        // optionally push aux-flags
        if(!coin.CoinbaseIgnoreAuxFlags && !string.IsNullOrEmpty(BlockTemplate.CoinbaseAux?.Flags))
            ops.Add(Op.GetPushOp(BlockTemplate.CoinbaseAux.Flags.HexToByteArray()));

        // push timestamp
        ops.Add(Op.GetPushOp(now));

        // push placeholder
        ops.Add(Op.GetPushOp(0));

        return new Script(ops);
    }

    protected virtual Transaction CreateOutputTransaction()
    {
        rewardToPool = new Money(BlockTemplate.CoinbaseValue, MoneyUnit.Satoshi);
        var tx = Transaction.Create(network);

        if(coin.HasPayee)
            rewardToPool = CreatePayeeOutput(tx, rewardToPool);

        if(coin.HasMasterNodes)
            rewardToPool = CreateMasternodeOutputs(tx, rewardToPool);

        if(coin.HasFounderFee)
            rewardToPool = CreateFounderOutputs(tx, rewardToPool);

        if(coin.HasFortuneReward)
            rewardToPool = CreateFortuneOutputs(tx, rewardToPool);

        if(coin.HasMinerFund)
            rewardToPool = CreateMinerFundOutputs(tx, rewardToPool);

        if(coin.HasCommunityAddress)
            rewardToPool = CreateCommunityAddressOutputs(tx, rewardToPool);

        if(coin.HasCoinbaseDevReward)
            rewardToPool = CreateCoinbaseDevRewardOutputs(tx, rewardToPool);

        if(coin.HasCoinbaseStakingReward)
            rewardToPool = CreateCoinbaseStakingRewardOutputs(tx, rewardToPool);

        // extras brought from another patch (with fix)
        if(coin.HasCommunity)
            rewardToPool = CreateCommunityOutputs(tx, rewardToPool);

        if(coin.HasDataMining)
            rewardToPool = CreateDataMiningOutputs(tx, rewardToPool);

        if(coin.HasDeveloper)
            rewardToPool = CreateDeveloperOutputs(tx, rewardToPool);

        // Guard: do not let negative reward
        if(rewardToPool < Money.Zero)
            throw new InvalidOperationException("Coinbase over-allocated: outputs exceed subsidy+fees");

        // Remaining amount goes to the pool (if > 0)
        if(rewardToPool > Money.Zero)
            tx.Outputs.Add(rewardToPool, poolAddressDestination);

        return tx;
    }

    protected virtual Money CreatePayeeOutput(Transaction tx, Money reward)
    {
        if(payeeParameters?.PayeeAmount != null && payeeParameters.PayeeAmount.Value > 0)
        {
            var payeeReward = new Money(payeeParameters.PayeeAmount.Value, MoneyUnit.Satoshi);
            reward -= payeeReward;

            tx.Outputs.Add(payeeReward, BitcoinUtils.AddressToDestination(payeeParameters.Payee, network));
        }

        return reward;
    }

    // submissions: case-insensitive comparer already ensures case-insensitive keys
    protected bool RegisterSubmit(string extraNonce1, string extraNonce2, string nTime, string nonce)
    {
        // Null-safe concatenation: nulls result in empty segments
        var key = string.Concat(extraNonce1 ?? string.Empty,
                                extraNonce2 ?? string.Empty,
                                nTime ?? string.Empty,
                                nonce ?? string.Empty);

        return submissions.TryAdd(key, true);
    }


    protected byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
    {
        // build merkle-root
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version
        var version = BlockTemplate.Version;

        // Overt-ASIC boost
        if(versionMask.HasValue && versionBits.HasValue)
            version = (version & ~versionMask.Value) | (versionBits.Value & versionMask.Value);

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits)),
            HashPrevBlock = uint256.Parse(BlockTemplate.PreviousBlockhash),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(nTime),
            Nonce = nonce
        };

        return blockHeader.ToBytes();
    }

    protected virtual (Share Share, string BlockHex) ProcessShareInternal(
        StratumConnection worker, string extraNonce2, uint nTime, uint nonce, uint? versionBits)
    {
        var context = worker.ContextAs<BitcoinWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // hash block-header
        var headerBytes = SerializeHeader(coinbaseHash, nTime, nonce, context.VersionRollingMask, versionBits);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash, (ulong) nTime, BlockTemplate, coin, networkParams);
        var headerValue = new uint256(headerHash);

        // calc share-diff
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, headerHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = headerValue <= blockTargetValue;

        // test if the share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, nTime);
            result.BlockHash = blockHash.ToHexString();

            var blockBytes = SerializeBlock(headerBytes, coinbase);
            var blockHex = blockBytes.ToHexString();

            return (result, blockHex);
        }

        return (result, null);
    }

    protected virtual byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var extraNonce2Bytes = extraNonce2.HexToByteArray();

        using(var stream = new MemoryStream())
        {
            stream.Write(coinbaseInitial);
            stream.Write(extraNonce1Bytes);
            stream.Write(extraNonce2Bytes);
            stream.Write(coinbaseFinal);

            return stream.ToArray();
        }
    }

    // Reserve capacity, keep canonical VarInt via BitcoinStream
    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase)
    {
        var rawTransactionBuffer = BuildRawTransactionBuffer();
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for coinbase

        // Estimate final size to avoid reallocations
        // header + varint(max 5B) + coinbase + txs + PoS(1B optional) + MWEB(optional)
        int estimated = header.Length + 5 + coinbase.Length + rawTransactionBuffer.Length;

        // PoS coins append a single 0x00 that is replaced by the daemon's signature later
        if(isPoS) estimated += 1;

        byte[] mwebRaw = null;
        if(coin.HasMWEB)
        {
            var mweb = BlockTemplate.Extra.SafeExtensionDataAs<MwebBlockTemplateExtra>();
            mwebRaw = mweb?.Mweb?.HexToByteArray();
            if(mwebRaw != null) estimated += 1 /*separator*/ + mwebRaw.Length;
        }

        using var stream = new MemoryStream(estimated);
        var bs = new BitcoinStream(stream, true);

        // Header
        bs.ReadWrite(header);

        // Tx count (canonical VarInt)
        bs.ReadWriteAsVarInt(ref transactionCount);

        // Coinbase + rest of transactions
        bs.ReadWrite(coinbase);
        bs.ReadWrite(rawTransactionBuffer);

        // PoS: extra 0x00 (daemon replaces with signature)
        if(isPoS)
            bs.ReadWrite((byte) 0);

        // Litecoin MWEB appendix (if present)
        if(mwebRaw != null)
        {
            var separator = new byte[] { 0x01 };
            bs.ReadWrite(separator);
            bs.ReadWrite(mwebRaw);
        }

        return stream.ToArray();
    }

    // Pre-size the buffer to avoid reallocations while writing raw transactions
    protected virtual byte[] BuildRawTransactionBuffer()
    {
        // First pass: compute total byte length from hex strings (2 hex chars = 1 byte)
        int total = 0;
        foreach(var tx in BlockTemplate.Transactions)
            total += (tx?.Data?.Length ?? 0) >> 1;

        using var stream = total > 0 ? new MemoryStream(total) : new MemoryStream();

        foreach(var tx in BlockTemplate.Transactions)
        {
            var txRaw = tx.Data.HexToByteArray();
            stream.Write(txRaw, 0, txRaw.Length);
        }

        return stream.ToArray();
    }


    #region Masternodes

    protected MasterNodeBlockTemplateExtra masterNodeParameters;

    protected virtual Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        if(masterNodeParameters.Masternode != null)
        {
            Masternode[] masternodes;

            // Dash v13 Multi-Master-Nodes
            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var masterNode in masternodes)
                {
                    if(!string.IsNullOrEmpty(masterNode.Payee))
                    {
                        var payeeDestination = BitcoinUtils.AddressToDestination(masterNode.Payee, network);
                        var payeeReward = masterNode.Amount;

                        tx.Outputs.Add(payeeReward, payeeDestination);
                        reward -= payeeReward;
                    }
                }
            }
        }

        if(masterNodeParameters.SuperBlocks is { Length: > 0 })
        {
            foreach(var superBlock in masterNodeParameters.SuperBlocks)
            {
                var payeeAddress = BitcoinUtils.AddressToDestination(superBlock.Payee, network);
                var payeeReward = superBlock.Amount;

                tx.Outputs.Add(payeeReward, payeeAddress);
                reward -= payeeReward;
            }
        }

        if(!coin.HasPayee && !string.IsNullOrEmpty(masterNodeParameters.Payee))
        {
            var payeeAddress = BitcoinUtils.AddressToDestination(masterNodeParameters.Payee, network);
            var payeeReward = masterNodeParameters.PayeeAmount;

            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // Masternodes

    #region Fortune

    protected FortuneBlockTemplateExtra fortuneParameters;

    protected virtual Money CreateFortuneOutputs(Transaction tx, Money reward)
    {
        if(fortuneParameters.Fortune != null)
        {
            Fortune[] fortunes;
            if(fortuneParameters.Fortune.Type == JTokenType.Array)
                fortunes = fortuneParameters.Fortune.ToObject<Fortune[]>();
            else
                fortunes = new[] { fortuneParameters.Fortune.ToObject<Fortune>() };

            if(fortunes != null)
            {
                foreach(var Fortune in fortunes)
                {
                    if(!string.IsNullOrEmpty(Fortune.Payee))
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(Fortune.Payee, network);
                        var payeeReward = Fortune.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Fortune

    #region Founder

    protected FounderBlockTemplateExtra founderParameters;

    protected virtual Money CreateFounderOutputs(Transaction tx, Money reward)
    {
        if(founderParameters.Founder != null)
        {
            Founder[] founders;
            if(founderParameters.Founder.Type == JTokenType.Array)
                founders = founderParameters.Founder.ToObject<Founder[]>();
            else
                founders = new[] { founderParameters.Founder.ToObject<Founder>() };

            if(founders != null)
            {
                foreach(var Founder in founders)
                {
                    if(!string.IsNullOrEmpty(Founder.Payee))
                    {
                        var payeeAddress = BitcoinUtils.AddressToDestination(Founder.Payee, network);
                        var payeeReward = Founder.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                        reward -= payeeReward;
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Founder

    #region Minerfund

    protected MinerFundTemplateExtra minerFundParameters;

    protected virtual Money CreateMinerFundOutputs(Transaction tx, Money reward)
    {
        if(!string.IsNullOrEmpty(minerFundParameters.Addresses?.FirstOrDefault()))
        {
            var payeeReward = minerFundParameters.MinimumValue;

            var payeeAddress = BitcoinUtils.AddressToDestination(minerFundParameters.Addresses[0], network);
            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // Founder

    #region CommunityAddress

    protected virtual Money CreateCommunityAddressOutputs(Transaction tx, Money reward)
    {
        var value = BlockTemplate.CommunityAutonomousValue;
        var addr = BlockTemplate.CommunityAutonomousAddress;

        if(value > 0 && !string.IsNullOrEmpty(addr))
        {
            var payeeReward = value;
            var payeeAddress = BitcoinUtils.AddressToDestination(addr, network);

            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }

        return reward;
    }

    #endregion // CommunityAddres

    #region CoinbaseDevReward

    protected CoinbaseDevRewardTemplateExtra CoinbaseDevRewardParams;

    protected virtual Money CreateCoinbaseDevRewardOutputs(Transaction tx, Money reward)
    {
        if(CoinbaseDevRewardParams?.CoinbaseDevReward != null)
        {
            var items = new[] { CoinbaseDevRewardParams.CoinbaseDevReward.ToObject<CoinbaseDevReward>() };

            foreach(var it in items)
            {
                if(it == null || string.IsNullOrEmpty(it.ScriptPubkey))
                    continue;

                try
                {
                    var spk = new Script(it.ScriptPubkey.HexToByteArray());
                    var pay = it.Value;
                    if(pay <= Money.Zero)
                        continue;

                    tx.Outputs.Add(pay, spk);
                    reward -= pay;
                }
                catch
                {
                    // invalid script: ignore
                    logger?.Warn(() => $"CoinbaseDevReward: invalid script {it.ScriptPubkey}");
                }
            }
        }

        return reward;
    }

    #endregion // CoinbaseDevReward

    #region CoinbaseStakingReward

    protected CoinbaseStakingRewardTemplateExtra coinbaseStakingRewardParameters;

    protected virtual Money CreateCoinbaseStakingRewardOutputs(Transaction tx, Money reward)
    {
        if(!string.IsNullOrEmpty(coinbaseStakingRewardParameters.PayoutScript?.ScriptPubkey))
        {
            Script payeeAddress = new Script(coinbaseStakingRewardParameters.PayoutScript.ScriptPubkey.HexToByteArray());
            var payeeReward = coinbaseStakingRewardParameters.MinimumValue;
            tx.Outputs.Add(payeeReward, payeeAddress);
            reward -= payeeReward;
        }
        return reward;
    }

    #endregion // CoinbaseStakingReward

    #region Community

    protected CommunityBlockTemplateExtra communityParameters;

    protected virtual Money CreateCommunityOutputs(Transaction tx, Money reward)
    {
        var list = communityParameters?.Community;
        if(list != null)
        {
            foreach(var c in list)
            {
                if(string.IsNullOrEmpty(c.Script) || c.Amount <= Money.Zero)
                    continue;

                try
                {
                    var spk = new Script(c.Script.HexToByteArray());
                    tx.Outputs.Add(c.Amount, spk);
                    reward -= c.Amount;
                }
                catch
                {
                    // ignore
                }
            }
        }

        return reward;
    }

    #endregion //Community

    #region DataMining

    protected DataMiningBlockTemplateExtra dataminingParameters;

    protected virtual Money CreateDataMiningOutputs(Transaction tx, Money reward)
    {
        var list = dataminingParameters?.DataMining;
        if(list != null)
        {
            foreach(var d in list)
            {
                if(string.IsNullOrEmpty(d.Script) || d.Amount <= Money.Zero)
                    continue;

                try
                {
                    var spk = new Script(d.Script.HexToByteArray());
                    tx.Outputs.Add(d.Amount, spk);
                    reward -= d.Amount;
                }
                catch
                {
                    // ignore
                }
            }
        }

        return reward;
    }

    #endregion //DataMining

    #region Developer

    protected DeveloperBlockTemplateExtra developerParameters;

    protected virtual Money CreateDeveloperOutputs(Transaction tx, Money reward)
    {
        var list = developerParameters?.Developer;
        if(list != null)
        {
            foreach(var d in list)
            {
                if(string.IsNullOrEmpty(d.Script) || d.Amount <= Money.Zero)
                    continue;

                try
                {
                    var spk = new Script(d.Script.HexToByteArray());
                    tx.Outputs.Add(d.Amount, spk);
                    reward -= d.Amount;
                }
                catch
                {
                    // ignore
                }
            }
        }

        return reward;
    }

    #endregion //Developer

    #region API-Surface

    public BlockTemplate BlockTemplate { get; protected set; }
    public double Difficulty { get; protected set; }

    public string JobId { get; protected set; }

    public void Init(BlockTemplate blockTemplate, string jobId,
        PoolConfig pc, BitcoinPoolConfigExtra extraPoolConfig,
        ClusterConfig cc, IMasterClock clock,
        IDestination poolAddressDestination, Network network,
        bool isPoS, double shareMultiplier, IHashAlgorithm coinbaseHasher,
        IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(poolAddressDestination);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        coin = pc.Template.As<BitcoinTemplate>();
        networkParams = coin.GetNetwork(network.ChainName);
        txVersion = coin.CoinbaseTxVersion;
        this.network = network;
        this.clock = clock;
        this.poolAddressDestination = poolAddressDestination;
        BlockTemplate = blockTemplate;
        JobId = jobId;

        var coinbaseString = !string.IsNullOrEmpty(cc.PaymentProcessing?.CoinbaseString) ?
            cc.PaymentProcessing?.CoinbaseString.Trim() : "Miningcore";

        scriptSigFinalBytes = new Script(Op.GetPushOp(Encoding.UTF8.GetBytes(coinbaseString))).ToBytes();

        Difficulty = new Target(System.Numerics.BigInteger.Parse(BlockTemplate.Target, NumberStyles.HexNumber)).Difficulty;

        extraNoncePlaceHolderLength = BitcoinConstants.ExtranoncePlaceHolderLength;
        this.isPoS = isPoS;
        this.shareMultiplier = shareMultiplier;

        txComment = !string.IsNullOrEmpty(extraPoolConfig?.CoinbaseTxComment) ?
            extraPoolConfig.CoinbaseTxComment : coin.CoinbaseTxComment;

        if(coin.HasMasterNodes)
        {
            masterNodeParameters = BlockTemplate.Extra.SafeExtensionDataAs<MasterNodeBlockTemplateExtra>();

            if(coin.HasSmartNodes)
            {
                if(masterNodeParameters.Extra?.ContainsKey("smartnode") == true)
                {
                    masterNodeParameters.Masternode = JToken.FromObject(masterNodeParameters.Extra["smartnode"]);
                }
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

        if(coin.HasFortuneReward)
            fortuneParameters = BlockTemplate.Extra.SafeExtensionDataAs<FortuneBlockTemplateExtra>();

        if(coin.HasMinerFund)
            minerFundParameters = BlockTemplate.Extra.SafeExtensionDataAs<MinerFundTemplateExtra>("coinbasetxn", "minerfund");

        if(coin.HasCoinbaseDevReward)
            CoinbaseDevRewardParams = BlockTemplate.Extra.SafeExtensionDataAs<CoinbaseDevRewardTemplateExtra>();

        if(coin.HasCoinbaseStakingReward)
            coinbaseStakingRewardParameters = BlockTemplate.Extra.SafeExtensionDataAs<CoinbaseStakingRewardTemplateExtra>("coinbasetxn", "stakingrewards");

        // extras
        if(coin.HasCommunity)
            communityParameters = BlockTemplate.Extra.SafeExtensionDataAs<CommunityBlockTemplateExtra>();

        if(coin.HasDataMining)
            dataminingParameters = BlockTemplate.Extra.SafeExtensionDataAs<DataMiningBlockTemplateExtra>();

        if(coin.HasDeveloper)
            developerParameters = BlockTemplate.Extra.SafeExtensionDataAs<DeveloperBlockTemplateExtra>();

        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;

        if(!string.IsNullOrEmpty(BlockTemplate.Target))
            blockTargetValue = new uint256(BlockTemplate.Target);
        else
        {
            var tmp = new Target(BlockTemplate.Bits.HexToByteArray());
            blockTargetValue = tmp.ToUInt256();
        }

        previousBlockHashReversedHex = BlockTemplate.PreviousBlockhash
            .HexToByteArray()
            .ReverseByteOrder()
            .ToHexString();

        BuildMerkleBranches();
        BuildCoinbase();

        jobParams = new object[]
        {
            JobId,
            previousBlockHashReversedHex,
            coinbaseInitialHex,
            coinbaseFinalHex,
            merkleBranchesHex,
            BlockTemplate.Version.ToStringHex8(),
            BlockTemplate.Bits,
            BlockTemplate.CurTime.ToStringHex8(),
            false
        };
    }

    public object GetJobParams(bool isNew)
    {
        jobParams[^1] = isNew;
        return jobParams;
    }

    public virtual (Share Share, string BlockHex) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce, string versionBits = null)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // validate nTime
        if(nTime.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of ntime");

        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);
        if(nTimeInt < BlockTemplate.CurTime || nTimeInt > ((DateTimeOffset) clock.Now).ToUnixTimeSeconds() + 7200)
            throw new StratumException(StratumError.Other, "ntime out of range");

        // validate nonce
        if(nonce.Length != 8)
            throw new StratumException(StratumError.Other, "incorrect size of nonce");

        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

        // validate version-bits (overt ASIC boost)
        uint versionBitsInt = 0;

        if(context.VersionRollingMask.HasValue && versionBits != null)
        {
            versionBitsInt = uint.Parse(versionBits, NumberStyles.HexNumber);

            // enforce that only bits covered by current mask are changed by miner
            if((versionBitsInt & ~context.VersionRollingMask.Value) != 0)
                throw new StratumException(StratumError.Other, "rolling-version mask violation");
        }

        // dupe check
        if(!RegisterSubmit(context.ExtraNonce1, extraNonce2, nTime, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        return ProcessShareInternal(worker, extraNonce2, nTimeInt, nonceInt, versionBitsInt);
    }

    #endregion // API-Surface
}

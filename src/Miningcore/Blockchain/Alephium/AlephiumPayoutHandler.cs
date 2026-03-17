using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Alephium.Configuration;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Util;
using Block = Miningcore.Persistence.Model.Block;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Alephium;

[CoinFamily(CoinFamily.Alephium)]
public class AlephiumPayoutHandler : PayoutHandlerBase, IPayoutHandler
{
    public AlephiumPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus)
        : base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        this.ctx = ctx;
    }

    protected readonly IComponentContext ctx;
    protected AlephiumClient alephiumClient;
    private string network;
    private AlephiumPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;

    protected override string LogCategory => "Alephium Payout Handler";

    #region IPayoutHandler

    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing?.Extra?.SafeExtensionDataAs<AlephiumPaymentProcessingConfigExtra>();

        logger = LogUtil.GetPoolScopedLogger(typeof(AlephiumPayoutHandler), pc);

        alephiumClient = AlephiumClientFactory.CreateClient(pc, cc, null);

        var infosChainParams = await Guard(() => alephiumClient.GetInfosChainParamsAsync(ct),
            ex => ReportAndRethrowApiError("Failed to get key params", ex));

        switch(infosChainParams?.NetworkId)
        {
            case 0:
                network = "mainnet";
                break;
            case 1:
            case 7:
                network = "testnet";
                break;
            case 4:
                network = "devnet";
                break;
            default:
                throw new PaymentException($"Unsupported network type '{infosChainParams?.NetworkId}'");
        }
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        var coin = poolConfig.Template.As<AlephiumCoinTemplate>();
        const int pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            var page = blocks.Skip(i * pageSize).Take(pageSize).ToArray();

            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                Settlement blockRewardTransaction = null;
                FixedAssetOutput blockReward = null;
                BlockEntry blockInfo = null;

                // output index: 0 = mainchain, 1..2 = uncles
                var blockRewardTransactionIndex = 0;

                var isBlockInMainChain = await Guard(() => alephiumClient.GetBlockflowIsBlockInMainChainAsync((string) block.Hash, ct),
                    ex => logger.Debug(ex));

                if(!isBlockInMainChain)
                {
                    block.Type = AlephiumConstants.BlockTypeUncle;

                    // info do “ghost uncle”
                    // "ghost uncle" Info
                    blockInfo = await Guard(() => alephiumClient.UncleHashAsync((string) block.Hash, ct),
                        ex => logger.Debug(ex));

                    if(blockInfo == null)
                    {
                        result.Add(MarkOrphan(block, coin, reason: "it's not on the chain and not even a 'ghost' uncle"));
                        continue;
                    }

                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] is a possible ghost uncle. It contains {blockInfo.Transactions.Count} transaction(s)");

                    // Only reward transactions (no inputs)
                    blockRewardTransaction = blockInfo.Transactions
                        .Where(x => x.Unsigned?.Inputs?.Count < 1)
                        .LastOrDefault();

                    // the index of the uncle in this list
                    var ghostUncleIndex = blockInfo.GhostUncles
                        ?.ToList()
                        .FindIndex(u => u.BlockHash == block.Hash) ?? -1;

                    if(ghostUncleIndex < 0)
                    {
                        // not in the list => treat as orphan
                        result.Add(MarkOrphan(block, coin, reason: "ghost uncle not found in reward tx"));
                        continue;
                    }

                    blockRewardTransactionIndex = ghostUncleIndex + 1; // +1 because of the mainchain reward at [0]
                }
                else
                {
                    block.Type = AlephiumConstants.BlockTypeBlock;

                    blockInfo = await Guard(() => alephiumClient.HashAsync((string) block.Hash, ct),
                        ex => logger.Debug(ex));

                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {blockInfo.Transactions.Count} transaction(s)");

                    blockRewardTransaction = blockInfo.Transactions
                        .Where(x => x.Unsigned?.Inputs?.Count < 1)
                        .LastOrDefault();
                }

                logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {(blockRewardTransaction == null ? 0 : blockRewardTransaction.Unsigned?.FixedOutputs?.Count ?? 0)} transaction(s) related to the block reward");

                if(blockRewardTransaction != null && blockRewardTransaction.Unsigned?.FixedOutputs != null)
                {
                    // miners' wallet addresses
                    var walletMinersAddresses = await Guard(() => alephiumClient.GetMinersAddressesAsync(ct),
                        ex => logger.Debug(ex));

                    blockReward = blockRewardTransaction.Unsigned.FixedOutputs.ElementAtOrDefault(blockRewardTransactionIndex);

                    // validates if the destination is indeed one of the wallet's mining addresses
                    if(blockReward == null || walletMinersAddresses?.Addresses?.Contains(blockReward.Address) != true)
                        blockReward = null;

                    logger.Debug(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] contains {(blockReward == null ? 0 : 1)} transaction related to our wallet miner's addresses");

                    if(blockReward != null)
                    {

                        // calculation of "confirmation" progress (actually, locktime until spendable)
                        if(extraPoolPaymentProcessingConfig?.BlockRewardsLockTime == null)
                        {
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] uses the default block reward lock mechanism for minimum confirmations calculation");

                            var txLock = (decimal) blockReward.LockTime;
                            block.ConfirmationProgress = Math.Min(1.0d,
                                (double) ((AlephiumUtils.UnixTimeStampForApi(clock.Now) - blockInfo.Timestamp) / (txLock - blockInfo.Timestamp)));
                        }
                        else
                        {
                            logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [{block.Hash}] uses a custom [{network}] block rewards lock time: [{extraPoolPaymentProcessingConfig?.BlockRewardsLockTime}] minute(s)");

                            var customMs = (decimal) extraPoolPaymentProcessingConfig.BlockRewardsLockTime * 60m * 1000m;
                            block.ConfirmationProgress = Math.Min(1.0d,
                                (double) ((AlephiumUtils.UnixTimeStampForApi(clock.Now) - blockInfo.Timestamp) / customMs));
                        }

                        result.Add(block);
                        messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                        if(block.ConfirmationProgress >= 1)
                        {
                            block.Status = BlockStatus.Confirmed;
                            block.ConfirmationProgress = 1;

                            block.Reward = 0;
                            block.Reward = AlephiumUtils.ConvertNumberFromApi(blockReward.AttoAlphAmount) / AlephiumConstants.SmallestUnit;

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} [{block.Hash}] worth {FormatAmount(block.Reward)}");
                            messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                        }

                        continue;
                    }
                }

                // if we got here, we didn't receive reward in our wallet -> orphan
                result.Add(MarkOrphan(block, coin, reason: "it's not on the chain"));
            }
        }

        return result.ToArray();
    }

    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // if nothing to pay, don't try
        var amounts = balances.Where(x => x.Amount > 0).ToDictionary(x => x.Address, x => x.Amount);
        if(amounts.Count == 0)
            return;

        var infosChainParams = await Guard(() => alephiumClient.GetInfosChainParamsAsync(ct));
        var info = await Guard(() => alephiumClient.GetInfosInterCliquePeerInfoAsync(ct));

        if(infosChainParams?.NetworkId != 7) // outside dev-test?
        {
            if(info?.Count < 1)
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peer(s)");
                return;
            }
        }

        var balancesTotal = amounts.Sum(x => x.Value);
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        // group by group (ALPH only allows multiple recipients if they are from the same group)
        var groupingAmounts = new List<KeyValuePair<string, decimal>>[]
        {
            new(), new(), new(), new(),
        };

        logger.Info(() => $"[{LogCategory}] Validating addresses...");
        foreach(var pair in amounts)
        {
            logger.Debug(() => $"[{LogCategory}] Address {pair.Key} with amount [{FormatAmount(pair.Value)}]");
            var validity = await Guard(() => alephiumClient.GetAddressesAddressGroupAsync(pair.Key, ct));
            if(validity == null || validity.Group1 < 0 || validity.Group1 > 3)
                logger.Warn(() => $"[{LogCategory}] Address {pair.Key} is not valid!");
            else
            {
                logger.Debug(() => $"[{LogCategory}] Address {pair.Key} belongs to group [{validity.Group1}]");
                groupingAmounts[validity.Group1].Add(pair);
            }
        }

        var walletWasUnlocked = false;

        try
        {
        retry:
            // wallet status
            var status = await alephiumClient.NameAsync(extraPoolPaymentProcessingConfig.WalletName, ct);

            // unlock if necessary
            if(status.Locked)
            {
                await UnlockWallet(ct);
                walletWasUnlocked = true;
            }

            // addresses of pool wallet
            var walletAddresses = await alephiumClient.NameAddressesAsync(extraPoolPaymentProcessingConfig.WalletName, ct);
            if(walletAddresses?.Addresses1 == null || walletAddresses.Addresses1.Count < 4)
            {
                logger.Warn(() => $"[{LogCategory}] Pool payment wallet name: {extraPoolPaymentProcessingConfig.WalletName} must have 4 miner's addresses. Please fix it");
                return;
            }

            // wallet balances
            var walletBalances = await alephiumClient.NameBalancesAsync(extraPoolPaymentProcessingConfig.WalletName, ct);
            var walletBalanceTotal = AlephiumUtils.ConvertNumberFromApi(walletBalances.TotalBalance) / AlephiumConstants.SmallestUnit;
            var walletBalanceLocked = walletBalances.Balances1
                .Sum(x => (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit));
            var walletBalanceAvailable = walletBalanceTotal - walletBalanceLocked;

            logger.Info(() => $"[{LogCategory}] Current wallet balance - Total: [{FormatAmount(walletBalanceTotal)}] - Locked: [{FormatAmount(walletBalanceLocked)}] - Available: [{FormatAmount(walletBalanceAvailable)}]");

            if(walletBalanceAvailable < balancesTotal)
            {
                logger.Warn(() => $"[{LogCategory}] Wallet balance currently short of {FormatAmount(balancesTotal - walletBalanceAvailable)}. Will try again");
                return;
            }

            // does any address cover the total?
            var anyPoolAddress = walletBalances.Balances1
                .FirstOrDefault(x =>
                    ((AlephiumUtils.ConvertNumberFromApi(x.Balance) / AlephiumConstants.SmallestUnit) -
                     (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit)) >= balancesTotal);

            if(string.IsNullOrEmpty(anyPoolAddress?.Address))
            {
                logger.Warn(() => $"[{LogCategory}] No pool wallet address can cover that transaction");

                // move funds from 2nd richest to richest
                var wealthyPoolAddress = walletBalances.Balances1
                    .OrderByDescending(x =>
                        (AlephiumUtils.ConvertNumberFromApi(x.Balance) / AlephiumConstants.SmallestUnit) -
                        (AlephiumUtils.ConvertNumberFromApi(x.LockedBalance) / AlephiumConstants.SmallestUnit))
                    .Take(2)
                    .ToArray();

                if(wealthyPoolAddress.Length < 2)
                {
                    logger.Warn(() => $"[{LogCategory}] Not enough wallet addresses to consolidate funds");
                    return;
                }

                var secondAvail = (AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].Balance) / AlephiumConstants.SmallestUnit) -
                                  (AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].LockedBalance) / AlephiumConstants.SmallestUnit);

                if(secondAvail <= 0)
                {
                    logger.Info(() => $"[{LogCategory}] All available funds have already been moved to pool wallet address {wealthyPoolAddress[0].Address} [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[0].Balance) / AlephiumConstants.SmallestUnit)}]");
                    return;
                }

                logger.Info(() => $"[{LogCategory}] We will now move the funds from pool wallet address {wealthyPoolAddress[1].Address} - Total: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].Balance) / AlephiumConstants.SmallestUnit)}] - Locked: [{FormatAmount(AlephiumUtils.ConvertNumberFromApi(wealthyPoolAddress[1].LockedBalance) / AlephiumConstants.SmallestUnit)}] - Available: [{FormatAmount(secondAvail)}] - to pool wallet address {wealthyPoolAddress[0].Address}");

                var bodyChangeWealthyActiveAddress = new ChangeActiveAddress
                {
                    Address = wealthyPoolAddress[1].Address,
                };

                await Guard(() => alephiumClient.NameChangeActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, bodyChangeWealthyActiveAddress, ct),
                    ex => logger.Warn(() => $"[{LogCategory}] Change active address failed"));

                logger.Debug(() => $"[{LogCategory}] Pool wallet address {wealthyPoolAddress[1].Address} is now the active address");

                var wealthyPoolAddressUtxos = await alephiumClient.GetAddressesAddressUtxosAsync(wealthyPoolAddress[1].Address, ct);
                if(!string.IsNullOrEmpty(wealthyPoolAddressUtxos?.Warning))
                {
                    logger.Warn(() => $"[{LogCategory}] Pool wallet address: {wealthyPoolAddress[1].Address} maybe can't be used anymore: {wealthyPoolAddressUtxos.Warning}. Please fix it");
                    return;
                }

                var inputWealthyUtxos = wealthyPoolAddressUtxos.Utxos
                    .Where(x => x.LockTime <= AlephiumUtils.UnixTimeStampForApi(clock.Now))
                    .Select(x => new OutputRef { Hint = x.Ref.Hint, Key = x.Ref.Key, })
                    .ToArray();

                logger.Debug(() => $"[{LogCategory}] Pool wallet address {wealthyPoolAddress[1].Address} has currently {inputWealthyUtxos.Length} (unlocked) UTXO(s)");

                Sweep destinationSweep;
                TransferResults txSweep;

                var inputWealthyGas = AlephiumConstants.GasPerInput * inputWealthyUtxos.Length;
                var outputWealthyGas = AlephiumConstants.GasPerOutput;
                var wealthyTxGas = inputWealthyGas + outputWealthyGas + AlephiumConstants.TxBaseGas + AlephiumConstants.P2pkUnlockGas + AlephiumConstants.GasPerOutput;
                var wealthyEstimatedGasAmount = Math.Max(AlephiumConstants.MinGasPerTx, wealthyTxGas);

                if(wealthyEstimatedGasAmount > AlephiumConstants.MaxGasPerTx)
                {
                    logger.Warn(() => $"[{LogCategory}] Estimated necessary gas amount [{wealthyEstimatedGasAmount}] exceeds the maximum possible per transaction [{AlephiumConstants.MaxGasPerTx}]. Letting Swagger pick inputs, only destination provided");

                    destinationSweep = new Sweep { ToAddress = wealthyPoolAddress[0].Address, };
                    txSweep = await Guard(() => alephiumClient.NameSweepAllAddressesAsync(extraPoolPaymentProcessingConfig.WalletName, destinationSweep, ct),
                        ex => ReportAndRethrowApiError("Failed to Sweep all wealthy active addresses", ex, false));
                }
                else
                {
                    logger.Debug(() => $"[{LogCategory}] Estimated necessary gas amount: {wealthyEstimatedGasAmount}");
                    destinationSweep = new Sweep { ToAddress = wealthyPoolAddress[0].Address, GasAmount = wealthyEstimatedGasAmount, };
                    txSweep = await Guard(() => alephiumClient.NameSweepActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, destinationSweep, ct),
                        ex => ReportAndRethrowApiError("Failed to Sweep wealthy active address", ex, false));
                }

                if(txSweep?.Results == null)
                    return;

                if(txSweep.Results.Count < 1)
                    logger.Warn(() => $"[{LogCategory}] Sweep transaction failed to return a transaction id");
                else
                {
                    foreach(var r in txSweep.Results)
                        logger.Info(() => $"[{LogCategory}] Sweep transaction id: {r.TxId}, FromGroup: {r.FromGroup}, ToGroup: {r.ToGroup}");
                }

                goto retry;
            }

            // address that covers the total
            var inputAddress = walletAddresses.Addresses1.FirstOrDefault(x => x.Address == anyPoolAddress.Address);
            if(string.IsNullOrEmpty(inputAddress?.Address))
            {
                logger.Warn(() => $"[{LogCategory}] Pool wallet address {anyPoolAddress.Address} does not exist anymore. Please fix it");
                return;
            }

            logger.Info(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} has enough funds - Total: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.Balance) / AlephiumConstants.SmallestUnit))}] - Locked: [{FormatAmount((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.LockedBalance) / AlephiumConstants.SmallestUnit))}] - Available: [{FormatAmount(((AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.Balance) / AlephiumConstants.SmallestUnit) - (AlephiumUtils.ConvertNumberFromApi(anyPoolAddress.LockedBalance) / AlephiumConstants.SmallestUnit)))}]");

            logger.Info(() => $"[{LogCategory}] Change active address");
            var bodyChangeActiveAddress = new ChangeActiveAddress { Address = inputAddress.Address, };
            await Guard(() => alephiumClient.NameChangeActiveAddressAsync(extraPoolPaymentProcessingConfig.WalletName, bodyChangeActiveAddress, ct),
                ex => logger.Warn(() => $"[{LogCategory}] Change active address failed"));

            logger.Debug(() => $"[{LogCategory}] Pool wallet address {inputAddress.Address} is now the active address");

            // process each group
            for(var g = 0; g < groupingAmounts.Length; g++)
            {
                var groupList = groupingAmounts[g];
                var initialTotalAddresses = groupList.Count;
                if(initialTotalAddresses <= 0)
                    continue;

                var groupTotalBalance = groupList.Sum(x => x.Value);
                logger.Info(() => $"[{LogCategory}] Processing group [{g}] containing {initialTotalAddresses} address(es), total amount [{FormatAmount(groupTotalBalance)}]");

                logger.Info(() => $"[{LogCategory}] 1/3) Build the transaction");

                var inputAddressUtxos = await alephiumClient.GetAddressesAddressUtxosAsync(inputAddress.Address, ct);
                if(!string.IsNullOrEmpty(inputAddressUtxos?.Warning))
                {
                    logger.Warn(() => $"[{LogCategory}] Pool wallet address: {anyPoolAddress.Address} maybe can't be used anymore: {inputAddressUtxos.Warning}. Please fix it");
                    continue;
                }

                var inputUtxos = inputAddressUtxos.Utxos
                    .Where(x => x.LockTime <= AlephiumUtils.UnixTimeStampForApi(clock.Now))
                    .Select(x => new OutputRef { Hint = x.Ref.Hint, Key = x.Ref.Key, })
                    .ToArray();

                var inputGas = AlephiumConstants.GasPerInput * inputUtxos.Length;
                var outputGas = AlephiumConstants.GasPerOutput * groupList.Count;
                var txGas = inputGas + outputGas + AlephiumConstants.TxBaseGas + AlephiumConstants.P2pkUnlockGas + AlephiumConstants.GasPerOutput;
                var estimatedGasAmount = Math.Max(AlephiumConstants.MinGasPerTx, txGas);

                if(estimatedGasAmount > AlephiumConstants.MaxGasPerTx)
                {
                    logger.Warn(() => $"[{LogCategory}] Estimated necessary gas amount [{estimatedGasAmount}] exceeds the maximum possible per transaction [{AlephiumConstants.MaxGasPerTx}]. We need to remove addresses.");
                    var numberOfAddressesToRemove = (int) Math.Ceiling((decimal) (estimatedGasAmount - AlephiumConstants.MaxGasPerTx) / AlephiumConstants.GasPerOutput);

                    if(numberOfAddressesToRemove >= initialTotalAddresses)
                    {
                        logger.Warn(() => $"[{LogCategory}] Need to remove {numberOfAddressesToRemove} address(es) but only {initialTotalAddresses} available. Skipping group.");
                        continue;
                    }

                    while(groupList.Count > (initialTotalAddresses - numberOfAddressesToRemove))
                        groupList.RemoveAt(groupList.Count - 1);

                    groupTotalBalance = groupList.Sum(x => x.Value);
                    logger.Info(() => $"[{LogCategory}] Group {g} now has {groupList.Count} address(es), total amount [{FormatAmount(groupTotalBalance)}]");

                    estimatedGasAmount = AlephiumConstants.MaxGasPerTx;
                }

                logger.Debug(() => $"[{LogCategory}] Estimated necessary gas amount: {estimatedGasAmount} [{FormatAmount(((estimatedGasAmount * AlephiumConstants.DefaultGasPrice) / AlephiumConstants.SmallestUnit))}]");

                // if KeepTransactionFees == true, deduct fee per address, but never let it go negative
                var feeTotalAtto = (decimal) estimatedGasAmount * AlephiumConstants.DefaultGasPrice;
                var perAddressFeeAtto = groupList.Count > 0 ? feeTotalAtto / groupList.Count : 0m;

                var batchDestinations = groupList.Select(x =>
                {
                    var grossAtto = x.Value * AlephiumConstants.SmallestUnit;
                    var netAtto = (extraPoolPaymentProcessingConfig?.KeepTransactionFees == true)
                        ? Math.Max(0m, grossAtto - perAddressFeeAtto)
                        : grossAtto;

                    return new Terminus
                    {
                        Address = x.Key,
                        AttoAlphAmount = AlephiumUtils.ConvertNumberForApi(netAtto),
                    };
                }).ToArray();

                var destinationsTransaction = new BuildSettlement
                {
                    FromPublicKey = inputAddress.PublicKey,
                    Destinations = batchDestinations,
                    Utxos = inputUtxos,
                    GasAmount = estimatedGasAmount,
                };

                var txBuild = await Guard(() => alephiumClient.PostTransactionsBuildAsync(destinationsTransaction, ct),
                    ex => logger.Warn(() => $"[{LogCategory}] Build transaction failed"));

                if(string.IsNullOrEmpty(txBuild?.TxId))
                    continue;

                logger.Info(() => $"[{LogCategory}] Unsigned transaction {txBuild.UnsignedTx} with txId {txBuild.TxId}");

                logger.Info(() => $"[{LogCategory}] 2/3) Sign the transaction");
                var signTxBuild = new Sign { Data = txBuild.TxId, };

                var txSign = await Guard(() => alephiumClient.NameSignAsync(extraPoolPaymentProcessingConfig.WalletName, signTxBuild, ct),
                    ex => logger.Warn(() => $"[{LogCategory}] Sign transaction failed"));

                if(string.IsNullOrEmpty(txSign?.Signature))
                    continue;

                logger.Info(() => $"[{LogCategory}] Unsigned transaction signature {txSign.Signature}");

                logger.Info(() => $"[{LogCategory}] 3/3) Submit signed transaction to the network");
                var submitTxSign = new SubmitSettlement
                {
                    UnsignedTx = txBuild.UnsignedTx,
                    Signature = txSign.Signature,
                };

                var txSubmit = await Guard(() => alephiumClient.PostTransactionsSubmitAsync(submitTxSign, ct),
                    ex => logger.Warn(() => $"[{LogCategory}] Submit signed transaction failed"));

                if(string.IsNullOrEmpty(txSubmit?.TxId))
                {
                    logger.Warn(() => $"[{LogCategory}] Payment transaction failed to return a transaction id");
                    continue;
                }

                logger.Info(() => $"[{LogCategory}] Payment transaction id: {txSubmit.TxId}");

                var successBalances = groupList
                    .Select(x => new Balance
                    {
                        PoolId = poolConfig.Id,
                        Address = x.Key,
                        Amount = x.Value,
                    })
                    .ToArray();

                await PersistPaymentsAsync(successBalances, txSubmit.TxId);

                var feePaid = feeTotalAtto / AlephiumConstants.SmallestUnit;
                NotifyPayoutSuccess(poolConfig.Id, successBalances, new[] { txSubmit.TxId }, feePaid);
            }
        }
        finally
        {
            // guarantee relock if the handler unlocked
            if(walletWasUnlocked)
                await LockWallet(ct);
        }
    }

    public override double AdjustShareDifficulty(double difficulty)
    {
        return difficulty * AlephiumConstants.Pow2xDiff1TargetNumZero;
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort * AlephiumConstants.Pow2xDiff1TargetNumZero;
    }

    #endregion // IPayoutHandler

    private static Block MarkOrphan(Block block, CoinTemplate coin, string reason)
    {
        block.Status = BlockStatus.Orphaned;
        block.Reward = 0;
        // logging and notification happen outside
        return block;
    }

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg) { }
    }

    // mantém a assinatura
    private void ReportAndRethrowApiError(string action, Exception ex, bool rethrow = true)
    {
        var error = ex.Message;
        if(ex is AlephiumApiException apiException && apiException.Response != null)
            error = apiException.Response;

        logger.Warn(() => $"{action}: {error}");

        if(rethrow)
            throw ex; // não use 'throw;' fora de catch
    }

    private async Task UnlockWallet(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Unlocking wallet: {extraPoolPaymentProcessingConfig.WalletName}");

        var walletPassword = extraPoolPaymentProcessingConfig?.WalletPassword ?? string.Empty;

        await Guard(() => alephiumClient.NameUnlockAsync(
                        extraPoolPaymentProcessingConfig.WalletName,
                        new WalletUnlock { Password = walletPassword }, ct),
            ex =>
            {
                if(ex is AlephiumApiException apiException)
                {
                    var error = apiException.Response;
                    if(error != null && !error.ToLower().Contains("already unlocked"))
                        throw new PaymentException($"Failed to unlock wallet: {error}");
                }
                else
                    throw ex; // idem
            });

        logger.Info(() => $"[{LogCategory}] Wallet: {extraPoolPaymentProcessingConfig.WalletName} unlocked");
    }

    private async Task LockWallet(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Locking wallet: {extraPoolPaymentProcessingConfig.WalletName}");

        await Guard(() => alephiumClient.NameLockAsync(extraPoolPaymentProcessingConfig.WalletName, ct),
            ex => ReportAndRethrowApiError("Failed to lock wallet", ex));

        logger.Info(() => $"[{LogCategory}] Wallet: {extraPoolPaymentProcessingConfig.WalletName} is locked");
    }
}

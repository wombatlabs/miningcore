using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Blockchain.Equihash.Configuration;
using Miningcore.Blockchain.Equihash.DaemonRequests;
using Miningcore.Blockchain.Equihash.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using IBlockRepository = Miningcore.Persistence.Repositories.IBlockRepository;

namespace Miningcore.Blockchain.Equihash;

[CoinFamily(CoinFamily.Equihash)]
public class EquihashPayoutHandler : BitcoinPayoutHandler
{
    public EquihashPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(ctx, cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
    }

    protected EquihashPoolConfigExtra poolExtraConfig;
    protected bool supportsNativeShielding;
    protected bool supportsSendCurrency;
    protected bool supportsZSendManyPrivacyPolicy;
    protected Network network;
    protected EquihashCoinTemplate.EquihashNetworkParams chainConfig;
    protected RpcClient walletRpcClient;
    protected override string LogCategory => "Equihash Payout Handler";
    protected const decimal TransferFee = 0.0001m;
    protected const int ZMinConfirmations = 8;
    protected const string PrivacyPolicy = "AllowRevealedRecipients";

    #region IPayoutHandler

    public override async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        await base.ConfigureAsync(cc, pc, ct);

        poolExtraConfig = pc.Extra.SafeExtensionDataAs<EquihashPoolConfigExtra>();

        // detect network
        var blockchainInfoResponse = await rpcClient.ExecuteAsync<BlockchainInfo>(logger, BitcoinCommands.GetBlockchainInfo, ct);

        network = Network.GetNetwork(blockchainInfoResponse.Response.Chain.ToLower());

        chainConfig = pc.Template.As<EquihashCoinTemplate>().GetNetwork(network.ChainName);

        // when mining against a wallet-less node (Zebra), payouts go through a separate transparent
        // wallet daemon; the shielded (z_*) payout path and its capability probes do not apply
        if(chainConfig?.UseNodeCoinbaseTx == true)
        {
            var walletDaemon = pc.Daemons.FirstOrDefault(x => x.Category?.ToLower() == EquihashConstants.WalletDaemonCategory);

            if(walletDaemon != null)
                walletRpcClient = new RpcClient(walletDaemon, ctx.Resolve<JsonSerializerSettings>(), messageBus, pc.Id);
            else
                logger.Warn(() => $"[{LogCategory}] No wallet daemon (category '{EquihashConstants.WalletDaemonCategory}') configured; transparent payouts will not run until one is added");

            return;
        }

        // detect z_shieldcoinbase support
        var response = await rpcClient.ExecuteAsync<JObject>(logger, EquihashCommands.ZShieldCoinbase, ct);
        supportsNativeShielding = response.Error.Code != (int) BitcoinRPCErrorCode.RPC_METHOD_NOT_FOUND;

        // detect sendcurrency support
        var responseSendCurrency = await rpcClient.ExecuteAsync<JObject>(logger, EquihashCommands.SendCurrency, ct);
        supportsSendCurrency = responseSendCurrency.Error.Code != (int) BitcoinRPCErrorCode.RPC_METHOD_NOT_FOUND;

        // detect z_sendmany PrivacyPolicy support
        var responseZSendMany = await rpcClient.ExecuteAsync<string>(logger, EquihashCommands.ZSendMany, ct, new object[] { poolExtraConfig.ZAddress, new [] { new ZSendManyRecipient { Address = poolExtraConfig.ZAddress, Amount = 0.0m } }, ZMinConfirmations, TransferFee, "WrongPrivacyPolicy" }); // we willingly provide the wrong parameter for "PrivacyPolicy" in order to detect its support and more importantly not accidently altering the pool wallet
        supportsZSendManyPrivacyPolicy = responseZSendMany.Error?.Code == (int) BitcoinRPCErrorCode.RPC_INVALID_PARAMETER;
        if(responseZSendMany.Error?.Code != null)
            logger.Debug(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} 'PrivacyPolicy' support returned error: {responseZSendMany.Error?.Message} code {responseZSendMany.Error?.Code}");
    }

    public override async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        // coins whose coinbase is built by a wallet-less node (Zebra) cannot be classified with the
        // wallet gettransaction RPC the base handler uses; classify via getrawtransaction instead
        if(chainConfig?.UseNodeCoinbaseTx != true)
            return await base.ClassifyBlocksAsync(pool, blocks, ct);

        var coin = poolConfig.Template.As<CoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // block.TransactionConfirmationData holds the coinbase txid; getrawtransaction with
            // verbosity 1 returns its height and confirmations against the best chain
            var batch = page.Select(block => new RpcRequest(BitcoinCommands.GetRawTransaction,
                new object[] { block.TransactionConfirmationData, 1 })).ToArray();

            var results = await rpcClient.ExecuteBatchAsync(logger, ct, batch);

            for(var j = 0; j < results.Length; j++)
            {
                var cmdResult = results[j];
                var block = page[j];
                var txInfo = cmdResult.Response?.ToObject<ZCashRawTransaction>();

                if(cmdResult.Error != null)
                {
                    // -5 ("no such mempool or blockchain transaction") means the coinbase is no longer
                    // on the best chain -> orphaned. Any other error is treated as transient: the block
                    // is left untouched and reclassified on the next cycle rather than losing its reward.
                    if(cmdResult.Error.Code == -5)
                    {
                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;
                        result.Add(block);

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned due to daemon error {cmdResult.Error.Code}");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }

                    else
                        logger.Warn(() => $"[{LogCategory}] Daemon reports error '{cmdResult.Error.Message}' (Code {cmdResult.Error.Code}) for transaction {block.TransactionConfirmationData}");
                }

                // found off the best chain (or reporting no confirmations) -> orphaned
                else if(txInfo == null || txInfo.Confirmations <= 0)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned (confirmations {txInfo?.Confirmations ?? 0})");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }

                else if(txInfo.Confirmations < minConfirmations)
                {
                    // immature - the block reward set at block-discovery time (miner subsidy + fees)
                    // is what the node coinbase paid, so it is not overwritten here
                    block.ConfirmationProgress = Math.Min(1.0d, (double) txInfo.Confirmations / minConfirmations);
                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);
                }

                else
                {
                    // matured and spendable
                    block.Status = BlockStatus.Confirmed;
                    block.ConfirmationProgress = 1;
                    result.Add(block);

                    logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }
            }
        }

        return result.ToArray();
    }

    public override async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // wallet-less node (Zebra): pay transparently (t-addr -> t-addr) through the external wallet daemon
        if(chainConfig?.UseNodeCoinbaseTx == true)
        {
            await PayoutTransparentAsync(pool, balances, ct);
            return;
        }

        // Some projects like Veruscoin does not require shielding before being able to spend coins.
        // They can also sends coins from a t-address to t-addresses and z-addresses
        if(supportsSendCurrency)
            await PayoutSendCurrencyAsync(pool, balances, ct);
        else
            await PayoutZSendManyAsync(pool, balances, ct);
        
        // lock wallet
        logger.Info(() => $"[{LogCategory}] Locking wallet");

        await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletLock, ct);
    }

    /// <summary>
    /// Transparent (t-addr to t-addr) payout through the external wallet daemon, used when the coin
    /// mines against a wallet-less node (Zebra). The wallet holds the pool address key and exposes a
    /// Bitcoin-compatible sendmany RPC.
    /// </summary>
    private async Task PayoutTransparentAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        if(walletRpcClient == null)
        {
            logger.Error(() => $"[{LogCategory}] Cannot pay out: no wallet daemon (category '{EquihashConstants.WalletDaemonCategory}') is configured");
            NotifyPayoutFailure(poolConfig.Id, balances, "No wallet daemon configured", null);
            return;
        }

        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => Math.Round(x.Amount, 8));

        if(amounts.Count == 0)
            return;

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses");

        var identifier = !string.IsNullOrEmpty(clusterConfig.PaymentProcessing?.CoinbaseString) ?
            clusterConfig.PaymentProcessing.CoinbaseString.Trim() : "Miningcore";
        var comment = $"{identifier} Payment";

        object[] args;

        if(extraPoolPaymentProcessingConfig?.MinersPayTxFees == true)
        {
            args = new object[]
            {
                string.Empty, // default account
                amounts, // addresses and associated amounts
                1, // only spend funds covered by this many confirmations
                comment, // tx comment
                amounts.Keys.ToArray(), // distribute transaction fee equally over all recipients
            };
        }

        else
        {
            args = new object[]
            {
                string.Empty, // default account
                amounts, // addresses and associated amounts
            };
        }

        var didUnlockWallet = false;

        // send command
        tryTransfer:
        var result = await walletRpcClient.ExecuteAsync<string>(logger, BitcoinCommands.SendMany, ct, args);

        if(result.Error == null)
        {
            if(didUnlockWallet)
            {
                logger.Info(() => $"[{LogCategory}] Locking wallet");
                await walletRpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletLock, ct);
            }

            var txId = result.Response;

            if(string.IsNullOrEmpty(txId))
                logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} did not return a transaction id!");
            else
                logger.Info(() => $"[{LogCategory}] Payment transaction id: {txId}");

            await PersistPaymentsAsync(balances, txId);

            NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txId }, null);
        }

        else
        {
            if(result.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
            {
                if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                {
                    logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                    var unlockResult = await walletRpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                    {
                        extraPoolPaymentProcessingConfig.WalletPassword,
                        (object) 5 // unlock for N seconds
                    });

                    if(unlockResult.Error == null)
                    {
                        didUnlockWallet = true;
                        goto tryTransfer;
                    }

                    else
                        logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {unlockResult.Error.Message} code {unlockResult.Error.Code}");
                }

                else
                    logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
            }

            else
            {
                logger.Error(() => $"[{LogCategory}] {BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}");

                NotifyPayoutFailure(poolConfig.Id, balances, $"{BitcoinCommands.SendMany} returned error: {result.Error.Message} code {result.Error.Code}", null);
            }
        }
    }

    private async Task PayoutZSendManyAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        var coin = poolConfig.Template.As<CoinTemplate>();

        // Shield first
        if(supportsNativeShielding)
            await ShieldCoinbaseAsync(ct);
        else
            await ShieldCoinbaseEmulatedAsync(ct);

        // send in batches with no more than 50 recipients to avoid running into tx size limits
        var pageSize = 50;
        var pageCount = (int) Math.Ceiling(balances.Length / (double) pageSize);

        for(var i = 0; i < pageCount; i++)
        {
            var didUnlockWallet = false;

            // get a page full of balances
            var page = balances
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build args
            var amounts = page
                .Where(x => x.Amount > 0)
                .Select(x => new ZSendManyRecipient { Address = x.Address, Amount = Math.Round(x.Amount, 8) })
                .ToList();

            if(amounts.Count == 0)
                return;

            var pageAmount = amounts.Sum(x => x.Amount);

            // check shielded balance
            var balanceResponse = await rpcClient.ExecuteAsync<object>(logger, EquihashCommands.ZGetBalance, ct, new object[]
            {
                poolExtraConfig.ZAddress, // default account
                ZMinConfirmations, // only spend funds covered by this many confirmations
            });

            if(balanceResponse.Error != null || (decimal) (double) balanceResponse.Response - TransferFee < pageAmount)
            {
                if(balanceResponse.Error != null)
                    logger.Warn(() => $"[{LogCategory}] {EquihashCommands.ZGetBalance} returned error: {balanceResponse.Error.Message} code {balanceResponse.Error.Code}");
                else
                    logger.Info(() => $"[{LogCategory}] Insufficient shielded balance for payment of {FormatAmount(pageAmount)}");

                return;
            }

            logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(pageAmount)} to {page.Length} addresses");

            object[] args;
            
            // Mainly supported by latest releases of Zcash (ZEC)
            if(supportsZSendManyPrivacyPolicy)
            {
                logger.Debug(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} 'PrivacyPolicy' is supported...");

                args = new object[]
                {
                    poolExtraConfig.ZAddress, // default account
                    amounts, // addresses and associated amounts
                    ZMinConfirmations, // only spend funds covered by this many confirmations
                    TransferFee,
                    PrivacyPolicy // allow transactions with transparent recipients to be processed, which is not enabled by default for that coin because of severe privacy reinforcements
                };
            }
            else
            {
                args = new object[]
                {
                    poolExtraConfig.ZAddress, // default account
                    amounts, // addresses and associated amounts
                    ZMinConfirmations, // only spend funds covered by this many confirmations
                    TransferFee
                };
            }

            // send command
            tryTransfer:
            var response = await rpcClient.ExecuteAsync<string>(logger, EquihashCommands.ZSendMany, ct, args);

            if(response.Error == null)
            {
                var operationId = response.Response;

                // check result
                if(string.IsNullOrEmpty(operationId))
                    logger.Error(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} did not return an operation id!");
                else
                {
                    logger.Info(() => $"[{LogCategory}] Tracking payment operation id: {operationId}");

                    var continueWaiting = true;

                    while(continueWaiting)
                    {
                        var operationResultResponse = await rpcClient.ExecuteAsync<ZCashAsyncOperationStatus[]>(logger,
                            EquihashCommands.ZGetOperationResult, ct, new object[] { new object[] { operationId } });

                        if(operationResultResponse.Error == null &&
                           operationResultResponse.Response?.Any(x => x.OperationId == operationId) == true)
                        {
                            var operationResult = operationResultResponse.Response.First(x => x.OperationId == operationId);

                            if(!Enum.TryParse(operationResult.Status, true, out ZOperationStatus status))
                            {
                                logger.Error(() => $"Unrecognized operation status: {operationResult.Status}");
                                break;
                            }

                            switch(status)
                            {
                                case ZOperationStatus.Success:
                                    var txId = operationResult.Result?.Value<string>("txid") ?? string.Empty;
                                    logger.Info(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} completed with transaction id: {txId}");

                                    await PersistPaymentsAsync(page, txId);
                                    NotifyPayoutSuccess(poolConfig.Id, page, new[] { txId }, null);

                                    continueWaiting = false;
                                    continue;

                                case ZOperationStatus.Cancelled:
                                case ZOperationStatus.Failed:
                                    logger.Error(() => $"{EquihashCommands.ZSendMany} failed: {operationResult.Error.Message} code {operationResult.Error.Code}");
                                    NotifyPayoutFailure(poolConfig.Id, page, $"{EquihashCommands.ZSendMany} failed: {operationResult.Error.Message} code {operationResult.Error.Code}", null);

                                    continueWaiting = false;
                                    continue;
                            }
                        }

                        logger.Info(() => $"[{LogCategory}] Waiting for completion: {operationId}");

                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                }
            }

            else
            {
                if(response.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResponse = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                        {
                            extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if(unlockResponse.Error == null)
                        {
                            didUnlockWallet = true;
                            goto tryTransfer;
                        }

                        else
                        {
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {response.Error.Message} code {response.Error.Code}");
                            NotifyPayoutFailure(poolConfig.Id, page, $"{BitcoinCommands.WalletPassphrase} returned error: {response.Error.Message} code {response.Error.Code}", null);
                            break;
                        }
                    }

                    else
                    {
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                        NotifyPayoutFailure(poolConfig.Id, page, "Wallet is locked but walletPassword was not configured. Unable to send funds.", null);
                        break;
                    }
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} returned error: {response.Error.Message} code {response.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, page, $"{EquihashCommands.ZSendMany} returned error: {response.Error.Message} code {response.Error.Code}", null);
                }
            }
        }
    }
    
    private async Task PayoutSendCurrencyAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);
        
        var coin = poolConfig.Template.As<CoinTemplate>();
        
        logger.Info(() => $"[{LogCategory}] Shielding ZCash Coinbase funds is not required");
        
        // send in batches with no more than 50 recipients to avoid running into tx size limits
        var pageSize = 50;
        var pageCount = (int) Math.Ceiling(balances.Length / (double) pageSize);

        for(var i = 0; i < pageCount; i++)
        {
            var didUnlockWallet = false;

            // get a page full of balances
            var page = balances
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // build args
            var amounts = page
                .Where(x => x.Amount > 0)
                .Select(x => new SendCurrencyOutputs { Amount = Math.Round(x.Amount, 8), Address = x.Address })
                .ToArray();

            if(amounts.Length == 0)
                return;

            var pageAmount = amounts.Sum(x => x.Amount);

            logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(pageAmount)} to {page.Length} addresses");

            var args = new object[]
            {
                "*", // source: wildcard "*" means any addresses own by "wallet.dat"
                amounts, // addresses and associated amounts
            };

            // send command
            trySendCurrencyTransfer:
            var response = await rpcClient.ExecuteAsync<string>(logger, EquihashCommands.SendCurrency, ct, args);

            if(response.Error == null)
            {
                var operationId = response.Response;

                // check result
                if(string.IsNullOrEmpty(operationId))
                    logger.Error(() => $"[{LogCategory}] {EquihashCommands.SendCurrency} did not return an operation id!");
                else
                {
                    logger.Info(() => $"[{LogCategory}] Tracking payment operation id: {operationId}");

                    var continueWaiting = true;

                    while(continueWaiting)
                    {
                        var operationResultResponse = await rpcClient.ExecuteAsync<ZCashAsyncOperationStatus[]>(logger,
                            EquihashCommands.ZGetOperationResult, ct, new object[] { new object[] { operationId } });

                        if(operationResultResponse.Error == null &&
                           operationResultResponse.Response?.Any(x => x.OperationId == operationId) == true)
                        {
                            var operationResult = operationResultResponse.Response.First(x => x.OperationId == operationId);

                            if(!Enum.TryParse(operationResult.Status, true, out ZOperationStatus status))
                            {
                                logger.Error(() => $"Unrecognized operation status: {operationResult.Status}");
                                break;
                            }

                            switch(status)
                            {
                                case ZOperationStatus.Success:
                                    var txId = operationResult.Result?.Value<string>("txid") ?? string.Empty;
                                    logger.Info(() => $"[{LogCategory}] {EquihashCommands.SendCurrency} completed with transaction id: {txId}");

                                    await PersistPaymentsAsync(page, txId);
                                    NotifyPayoutSuccess(poolConfig.Id, page, new[] { txId }, null);

                                    continueWaiting = false;
                                    continue;

                                case ZOperationStatus.Cancelled:
                                case ZOperationStatus.Failed:
                                    logger.Error(() => $"{EquihashCommands.SendCurrency} failed: {operationResult.Error.Message} code {operationResult.Error.Code}");
                                    NotifyPayoutFailure(poolConfig.Id, page, $"{EquihashCommands.SendCurrency} failed: {operationResult.Error.Message} code {operationResult.Error.Code}", null);

                                    continueWaiting = false;
                                    continue;
                            }
                        }

                        logger.Info(() => $"[{LogCategory}] Waiting for completion: {operationId}");

                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                }
            }

            else
            {
                if(response.Error.Code == (int) BitcoinRPCErrorCode.RPC_WALLET_UNLOCK_NEEDED && !didUnlockWallet)
                {
                    if(!string.IsNullOrEmpty(extraPoolPaymentProcessingConfig?.WalletPassword))
                    {
                        logger.Info(() => $"[{LogCategory}] Unlocking wallet");

                        var unlockResponse = await rpcClient.ExecuteAsync<JToken>(logger, BitcoinCommands.WalletPassphrase, ct, new[]
                        {
                            extraPoolPaymentProcessingConfig.WalletPassword,
                            (object) 5 // unlock for N seconds
                        });

                        if(unlockResponse.Error == null)
                        {
                            didUnlockWallet = true;
                            goto trySendCurrencyTransfer;
                        }

                        else
                        {
                            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.WalletPassphrase} returned error: {response.Error.Message} code {response.Error.Code}");
                            NotifyPayoutFailure(poolConfig.Id, page, $"{BitcoinCommands.WalletPassphrase} returned error: {response.Error.Message} code {response.Error.Code}", null);
                            break;
                        }
                    }

                    else
                    {
                        logger.Error(() => $"[{LogCategory}] Wallet is locked but walletPassword was not configured. Unable to send funds.");
                        NotifyPayoutFailure(poolConfig.Id, page, "Wallet is locked but walletPassword was not configured. Unable to send funds.", null);
                        break;
                    }
                }

                else
                {
                    logger.Error(() => $"[{LogCategory}] {EquihashCommands.SendCurrency} returned error: {response.Error.Message} code {response.Error.Code}");

                    NotifyPayoutFailure(poolConfig.Id, page, $"{EquihashCommands.SendCurrency} returned error: {response.Error.Message} code {response.Error.Code}", null);
                }
            }
        }
    }

    #endregion // IPayoutHandler

    /// <summary>
    /// ZCash coins are mined into a t-addr (transparent address), but can only be
    /// spent to a z-addr (shielded address), and must be swept out of the t-addr
    /// in one transaction with no change.
    /// </summary>
    private async Task ShieldCoinbaseAsync(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Shielding ZCash Coinbase funds");

        var args = new object[]
        {
            poolConfig.Address, // source: pool's t-addr receiving coinbase rewards
            poolExtraConfig.ZAddress, // dest:   pool's z-addr
        };

        var response = await rpcClient.ExecuteAsync<ZCashShieldingResponse>(logger, EquihashCommands.ZShieldCoinbase, ct, args);

        if(response.Error != null)
        {
            if(response.Error.Code == (int)BitcoinRPCErrorCode.RPC_WALLET_INSUFFICIENT_FUNDS || response.Error.Code == (int)BitcoinRPCErrorCode.RPC_INVALID_PARAMS || response.Error.Code == (int)BitcoinRPCErrorCode.RPC_INVALID_PARAMETER)
                logger.Info(() => $"[{LogCategory}] No funds to shield: {response.Error.Message} code {response.Error.Code}");
            else
                logger.Error(() => $"[{LogCategory}] {EquihashCommands.ZShieldCoinbase} returned an unexpected error: {response.Error.Message} code {response.Error.Code}");

            return;
        }

        var operationId = response.Response.OperationId;

        logger.Info(() => $"[{LogCategory}] {EquihashCommands.ZShieldCoinbase} operation id: {operationId}");

        var continueWaiting = true;

        while(continueWaiting)
        {
            var operationResultResponse = await rpcClient.ExecuteAsync<ZCashAsyncOperationStatus[]>(logger,
                EquihashCommands.ZGetOperationResult, ct, new object[] { new object[] { operationId } });

            if(operationResultResponse.Error == null &&
               operationResultResponse.Response?.Any(x => x.OperationId == operationId) == true)
            {
                var operationResult = operationResultResponse.Response.First(x => x.OperationId == operationId);

                if(!Enum.TryParse(operationResult.Status, true, out ZOperationStatus status))
                {
                    logger.Error(() => $"Unrecognized operation status: {operationResult.Status}");
                    break;
                }

                switch(status)
                {
                    case ZOperationStatus.Success:
                        logger.Info(() => $"[{LogCategory}] {EquihashCommands.ZShieldCoinbase} successful");

                        continueWaiting = false;
                        continue;

                    case ZOperationStatus.Cancelled:
                    case ZOperationStatus.Failed:
                        logger.Error(() => $"{EquihashCommands.ZShieldCoinbase} failed: {operationResult.Error.Message} code {operationResult.Error.Code}");

                        continueWaiting = false;
                        continue;
                }
            }

            logger.Info(() => $"[{LogCategory}] Waiting for shielding operation completion: {operationId}");

            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task ShieldCoinbaseEmulatedAsync(CancellationToken ct)
    {
        logger.Info(() => $"[{LogCategory}] Shielding ZCash Coinbase funds (emulated)");

        // get t-addr unspent balance for just the coinbase address (pool wallet)
        var unspentResponse = await rpcClient.ExecuteAsync<Utxo[]>(logger, BitcoinCommands.ListUnspent, ct);

        if(unspentResponse.Error != null)
        {
            logger.Error(() => $"[{LogCategory}] {BitcoinCommands.ListUnspent} returned error: {unspentResponse.Error.Message} code {unspentResponse.Error.Code}");
            return;
        }

        var balance = unspentResponse.Response
            .Where(x => x.Spendable && x.Address == poolConfig.Address)
            .Sum(x => x.Amount);

        // make sure there's enough balance to shield after reserves
        if(balance - TransferFee <= TransferFee)
        {
            logger.Info(() => $"[{LogCategory}] Balance {FormatAmount(balance)} too small for emulated shielding");
            return;
        }

        logger.Info(() => $"[{LogCategory}] Transferring {FormatAmount(balance - TransferFee)} to pool's z-addr");

        // transfer to z-addr
        var recipient = new ZSendManyRecipient
        {
            Address = poolExtraConfig.ZAddress,
            Amount = balance - TransferFee
        };

        var args = new object[]
        {
            poolConfig.Address, // default account
            new object[] // addresses and associated amounts
            {
                recipient
            },
            1,
            TransferFee
        };

        // send command
        var sendResponse = await rpcClient.ExecuteAsync<string>(logger, EquihashCommands.ZSendMany, ct, args);

        if(sendResponse.Error != null)
        {
            logger.Error(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} returned error: {unspentResponse.Error.Message} code {unspentResponse.Error.Code}");
            return;
        }

        var operationId = sendResponse.Response;

        logger.Info(() => $"[{LogCategory}] {EquihashCommands.ZSendMany} operation id: {operationId}");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));

        var continueWaiting = true;

        do
        {
            var operationResultResponse = await rpcClient.ExecuteAsync<ZCashAsyncOperationStatus[]>(logger,
                EquihashCommands.ZGetOperationResult, ct, new object[] { new object[] { operationId } });

            if(operationResultResponse.Error == null &&
               operationResultResponse.Response?.Any(x => x.OperationId == operationId) == true)
            {
                var operationResult = operationResultResponse.Response.First(x => x.OperationId == operationId);

                if(!Enum.TryParse(operationResult.Status, true, out ZOperationStatus status))
                {
                    logger.Error(() => $"Unrecognized operation status: {operationResult.Status}");
                    break;
                }

                switch(status)
                {
                    case ZOperationStatus.Success:
                        var txId = operationResult.Result?.Value<string>("txid") ?? string.Empty;
                        logger.Info(() => $"[{LogCategory}] Transfer completed with transaction id: {txId}");

                        continueWaiting = false;
                        continue;

                    case ZOperationStatus.Cancelled:
                    case ZOperationStatus.Failed:
                        logger.Error(() => $"{EquihashCommands.ZSendMany} failed: {operationResult.Error.Message} code {operationResult.Error.Code}");

                        continueWaiting = false;
                        continue;
                }
            }

            logger.Info(() => $"[{LogCategory}] Waiting for shielding transfer completion: {operationId}");
        } while(continueWaiting && await timer.WaitForNextTickAsync(ct));
    }
}

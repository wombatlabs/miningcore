using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
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
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;
using Miningcore.Wallets;

namespace Miningcore.Blockchain.Kaspa;

[CoinFamily(CoinFamily.Kaspa)]
public class KaspaPayoutHandler : PayoutHandlerBase, IPayoutHandler
{
    public KaspaPayoutHandler(
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
    protected kaspad.KaspadRPC.KaspadRPCClient rpc;
    private ExternalWalletHttpClient httpWallet;
    private string network;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected override string LogCategory => "Kaspa Payout Handler";

    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);
        poolConfig = pc;
        clusterConfig = cc;
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<KaspaPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<KaspaPaymentProcessingConfigExtra>();
        logger = LogUtil.GetPoolScopedLogger(typeof(KaspaPayoutHandler), pc);

        // kaspad RPC
        var daemonEndpoints = pc.Daemons.Where(x => string.IsNullOrEmpty(x.Category)).ToArray();
        rpc = KaspaClientFactory.CreateKaspadRPCClient(
            daemonEndpoints,
            extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName);

        // descobrir network
        var stream = rpc.MessageStream(null, null, ct);
        try
        {
            var request = new kaspad.KaspadMessage { GetCurrentNetworkRequest = new kaspad.GetCurrentNetworkRequestMessage() };

            await Guard(() => stream.RequestStream.WriteAsync(request),
                ex => throw new PaymentException($"Error writing request to stream '{ex.GetType().Name}' : {ex}"));

            while (await stream.ResponseStream.MoveNext(ct))
            {
                var msg = stream.ResponseStream.Current;
                var err = msg.GetCurrentNetworkResponse?.Error?.Message;
                if(!string.IsNullOrEmpty(err))
                    throw new PaymentException($"Daemon reports: {err}");
                network = msg.GetCurrentNetworkResponse?.CurrentNetwork;
                break;
            }
        }
        finally { await stream.RequestStream.CompleteAsync(); }

        // HTTP wrapper
        var httpBase = extraPoolPaymentProcessingConfig?.WalletHttpBaseUrl?.Trim();
        if(!string.IsNullOrWhiteSpace(httpBase))
        {
            httpWallet = new ExternalWalletHttpClient(httpBase);
            try
            {
                var verRaw = await httpWallet.GetVersionAsync();
                logger.Info(() => $"[Kaspa Payout Handler] Wallet wrapper reachable, version raw: {verRaw}");
            }
            catch(Exception ex)
            {
                logger.Warn(() => $"[Kaspa Payout Handler] Wallet wrapper version check failed: {ex.Message}");
            }
        }
        else
            logger.Warn(() => $"[Kaspa Payout Handler] 'walletHttpBaseUrl' not configured – SimpleSend unavailable");
    }

    // === Classificação por BlueScore virtual (confirmações) ===
    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        if(blocks == null || blocks.Length == 0)
            return Array.Empty<Block>();

        var virtualBlue = await GetVirtualSelectedParentBlueScoreAsync(ct);
        var minConf = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? 120;
        if(minConf < 0) minConf = 0;

        var updated = new List<Block>();

        foreach(var b in blocks)
        {
            var blockDaa = (ulong)Math.Max(0, b.BlockHeight);
            var confs = virtualBlue > blockDaa ? virtualBlue - blockDaa : 0UL;

            b.ConfirmationProgress = minConf > 0
                ? Math.Min(1.0, (double) confs / (double) minConf)
                : 1.0;

            if(b.Status == BlockStatus.Pending && confs >= (ulong) minConf)
            {
                b.Status = BlockStatus.Confirmed;
                updated.Add(b);
            }
        }

        return updated.ToArray();
    }

    private async Task<ulong> GetVirtualSelectedParentBlueScoreAsync(CancellationToken ct)
    {
        var stream = rpc.MessageStream(null, null, ct);
        try
        {
            var req = new kaspad.KaspadMessage
            {
                GetVirtualSelectedParentBlueScoreRequest = new kaspad.GetVirtualSelectedParentBlueScoreRequestMessage()
            };

            await Guard(() => stream.RequestStream.WriteAsync(req),
                ex => throw new PaymentException($"Error writing request to stream '{ex.GetType().Name}' : {ex}"));

            while(await stream.ResponseStream.MoveNext(ct))
            {
                var msg = stream.ResponseStream.Current;
                var rsp = msg.GetVirtualSelectedParentBlueScoreResponse;
                if(rsp != null)
                {
                    var err = rsp.Error?.Message;
                    if(!string.IsNullOrEmpty(err))
                        throw new PaymentException($"Daemon reports: {err}");
                    return rsp.BlueScore;
                }
            }

            return 0;
        }
        finally { await stream.RequestStream.CompleteAsync(); }
    }

    // === Payouts via SimpleSend (/send with wrapper) **not being used** ===
    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        var amounts = balances
            .Where(x => x.Amount > 0)
            .OrderBy(x => x.Updated)
            .ThenByDescending(x => x.Amount)
            .ToDictionary(x => x.Address, x => x.Amount);

        if(amounts.Count == 0)
            return;

        var balancesTotal = amounts.Sum(x => x.Value);
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balancesTotal)} to {amounts.Count} addresses");

        // validar endereços
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        foreach(var pair in amounts)
        {
            var (_, err) = KaspaUtils.ValidateAddress(pair.Key, network, coin);
            if(err != null)
                logger.Warn(() => $"[{LogCategory}] Address {pair.Key} is not valid : {err.Message}");
        }

        if(httpWallet == null)
        {
            logger.Error(() => $"[{LogCategory}] walletHttpBaseUrl not configured. Cannot do payouts (SimpleSend).");
            return;
        }

        // best-effort info
        try
        {
            using var doc = await httpWallet.DetailsAsync();
            string printable = null;
            try
            {
                var root = doc.RootElement;
                if(root.TryGetProperty("json", out var je) && je.ValueKind != System.Text.Json.JsonValueKind.Null)
                    printable = je.ToString();
                else if(root.TryGetProperty("raw", out var raw) && raw.ValueKind == System.Text.Json.JsonValueKind.String)
                    printable = raw.GetString();
            }
            catch { /* ignore */ }
            logger.Info(() => $"[{LogCategory}] Wallet(details) via wrapper: {(printable ?? "(ok)")}"); 
        }
        catch(Exception ex)
        {
            logger.Warn(() => $"[{LogCategory}] Wallet(details) via wrapper failed: {ex.Message}");
        }

        static string ExtractTxId(System.Text.Json.JsonDocument doc)
        {
            try
            {
                var root = doc.RootElement;
                if(root.TryGetProperty("json", out var je) && je.ValueKind != System.Text.Json.JsonValueKind.Null)
                {
                    if(je.TryGetProperty("txid", out var j1) && j1.ValueKind == System.Text.Json.JsonValueKind.String) return j1.GetString();
                    if(je.TryGetProperty("txId", out var j2) && j2.ValueKind == System.Text.Json.JsonValueKind.String) return j2.GetString();
                    if(je.TryGetProperty("id",   out var j3) && j3.ValueKind == System.Text.Json.JsonValueKind.String) return j3.GetString();
                }
                if(root.TryGetProperty("raw", out var raw) && raw.ValueKind == System.Text.Json.JsonValueKind.String)
                    return raw.GetString();
            }
            catch { }
            return null;
        }

        var successBalances = new Dictionary<Balance, string>();

        foreach(var kv in amounts)
        {
            var to = kv.Key;
            var amountKas = kv.Value;

            try
            {
                using var doc = await httpWallet.SendAsync(to, amountKas.ToString("0.########"));
                var txid = ExtractTxId(doc) ?? $"send:{to}:{amountKas}";
                if(txid.StartsWith("send:"))
                    logger.Warn(() => $"[{LogCategory}] /send returned no explicit txid for {to}. Using placeholder: {txid}");
                else
                    logger.Info(() => $"[{LogCategory}] Sent {FormatAmount(amountKas)} → {to} (tx={txid})");

                var b = balances.First(x => x.Address == to && x.Amount == amountKas);
                successBalances.Add(new Balance
                {
                    PoolId = poolConfig.Id,
                    Address = to,
                    Amount = amountKas,
                }, txid);
            }
            catch(Exception ex)
            {
                logger.Error(() => $"[{LogCategory}] /send failed for {to}: {ex.Message}");
            }
        }

        if(!successBalances.Any())
        {
            logger.Warn(() => $"[{LogCategory}] No payouts were successfully sent.");
            return;
        }

        var txids = successBalances.Values.ToArray();
        logger.Info(() => $"[{LogCategory}] Kaspa payouts (SimpleSend): {txids.Length} tx(s) -> {string.Join(",", txids)}");

        await PersistPaymentsAsync(successBalances);
        NotifyPayoutSuccess(poolConfig.Id, successBalances.Keys.ToArray(), successBalances.Values.ToArray(), null);
    }

    public override double AdjustShareDifficulty(double difficulty)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        return coin.Symbol == "SPR"
            ? difficulty * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash
            : difficulty * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
    }

    public double AdjustBlockEffort(double effort)
    {
        var coin = poolConfig.Template.As<KaspaCoinTemplate>();
        return coin.Symbol == "SPR"
            ? effort * SpectreConstants.Pow2xDiff1TargetNumZero * (double) SpectreConstants.MinHash
            : effort * KaspaConstants.Pow2xDiff1TargetNumZero * (double) KaspaConstants.MinHash;
    }

    private class PaymentException : Exception
    {
        public PaymentException(string msg) : base(msg) { }
    }
}

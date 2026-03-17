// src/Miningcore/Api/Controllers/PoolApiController.cs
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Net;
using Autofac;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Model.Projections;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using NLog;

namespace Miningcore.Api.Controllers;

[Route("api/pools")]
[ApiController]
public class PoolApiController : ApiControllerBase
{
    public PoolApiController(IComponentContext ctx, IActionDescriptorCollectionProvider _adcp) : base(ctx)
    {
        statsRepo = ctx.Resolve<IStatsRepository>();
        blocksRepo = ctx.Resolve<IBlockRepository>();
        minerRepo = ctx.Resolve<IMinerRepository>();
        shareRepo = ctx.Resolve<IShareRepository>();
        paymentsRepo = ctx.Resolve<IPaymentRepository>();
        clock = ctx.Resolve<IMasterClock>();
        pools = ctx.Resolve<ConcurrentDictionary<string, IMiningPool>>();
        adcp = _adcp;
    }

    private readonly IStatsRepository statsRepo;
    private readonly IBlockRepository blocksRepo;
    private readonly IPaymentRepository paymentsRepo;
    private readonly IMinerRepository minerRepo;
    private readonly IShareRepository shareRepo;
    private readonly IMasterClock clock;
    private readonly IActionDescriptorCollectionProvider adcp;
    private readonly ConcurrentDictionary<string, IMiningPool> pools;

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();

    #region Actions

    [HttpGet]
    public async Task<GetPoolsResponse> Get(CancellationToken ct, [FromQuery] uint topMinersRange = 24)
    {
        // Build response by projecting only enabled pools
        var poolsInfo = await Task.WhenAll(clusterConfig.Pools
            .Where(x => x.Enabled)
            .Select(async config =>
            {
                // persisted stats (single query per pool)
                var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, config.Id, ct));

                // live pool instance (may be null)
                pools.TryGetValue(config.Id, out var poolInstance);

                // map basic pool info
                var result = config.ToPoolInfo(mapper, stats, poolInstance);

                // aggregate fields (keep these as separate queries to avoid huge joined payloads)
                result.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, config.Id, ct));
                result.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, config.Id, ct));
                result.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, config.Id, ct));
                result.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, config.Id, ct));
                result.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, config.Id, ct));

                // expose payout settings in a safe/consistent way
                var payoutConfig = config.PaymentProcessing;
                if(result?.PaymentProcessing != null && payoutConfig != null)
                {
                    // enum -> string to avoid CS0029 / CS1503
                    result.PaymentProcessing.PayoutScheme = payoutConfig.PayoutScheme.ToString();
                    result.PaymentProcessing.MinimumPayment = payoutConfig.MinimumPayment;

                    // PayoutSchemeConfig arrives as a JToken/dynamic in many setups; rehydrate into the API DTO
                    result.PaymentProcessing.PayoutSchemeConfig =
                        payoutConfig.PayoutSchemeConfig != null
                            ? payoutConfig.PayoutSchemeConfig.ToObject<ApiPoolPayoutSchemeConfig>()
                            : new ApiPoolPayoutSchemeConfig();

                    // Only show BlockFinderPercentage for PPLNSBF
                    if(payoutConfig.PayoutScheme != PayoutScheme.PPLNSBF && result.PaymentProcessing.PayoutSchemeConfig != null)
                        result.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
                }

                return result;
            }));

        return new GetPoolsResponse { Pools = poolsInfo };
    }



    [HttpGet("/api/help")]
    public ActionResult GetHelp()
    {
        var tmp = adcp.ActionDescriptors.Items
            .Where(x => x.AttributeRouteInfo != null)
            .Select(x =>
            {
                // Get and pad http method
                var method = x.ActionConstraints?.OfType<HttpMethodActionConstraint>().FirstOrDefault()?.HttpMethods.First();
                method = $"{method,-5}";

                return $"{method} -> {x.AttributeRouteInfo.Template}";
            });

        // convert curly braces
        var result = string.Join("\n", tmp).Replace("{", "<").Replace("}", ">") + "\n";

        return Content(result);
    }

    [HttpGet("/api/health-check")]
    public ActionResult GetHealthCheck()
    {
        return Content("👍");
    }

    [HttpGet("{poolId}")]
    public async Task<GetPoolResponse> GetPoolInfoAsync(string poolId, CancellationToken ct, [FromQuery] uint topMinersRange = 24)
    {
        var poolCfg = GetPool(poolId);

        // persisted stats
        var stats = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));

        // live pool instance (may be null)
        pools.TryGetValue(poolCfg.Id, out var poolInstance);

        var response = new GetPoolResponse
        {
            Pool = poolCfg.ToPoolInfo(mapper, stats, poolInstance)
        };

        // aggregates
        response.Pool.TotalPaid = await cf.Run(con => statsRepo.GetTotalPoolPaymentsAsync(con, poolCfg.Id, ct));
        response.Pool.TotalBlocks = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolCfg.Id, ct));
        response.Pool.TotalConfirmedBlocks = await cf.Run(con => blocksRepo.GetTotalConfirmedBlocksAsync(con, poolCfg.Id, ct));
        response.Pool.TotalPendingBlocks = await cf.Run(con => blocksRepo.GetTotalPendingBlocksAsync(con, poolCfg.Id, ct));
        response.Pool.BlockReward = await cf.Run(con => blocksRepo.GetLastConfirmedBlockRewardAsync(con, poolCfg.Id, ct));

        // last block time
        var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, poolCfg.Id, ct));
        response.Pool.LastPoolBlockTime = lastBlockTime;

        // payout section (use poolCfg, not a non-existent 'pool')
        var payoutConfig = poolCfg.PaymentProcessing;
        if(response.Pool.PaymentProcessing != null && payoutConfig != null)
        {
            response.Pool.PaymentProcessing.PayoutScheme = payoutConfig.PayoutScheme.ToString();
            response.Pool.PaymentProcessing.MinimumPayment = payoutConfig.MinimumPayment;

            response.Pool.PaymentProcessing.PayoutSchemeConfig =
                payoutConfig.PayoutSchemeConfig != null
                    ? payoutConfig.PayoutSchemeConfig.ToObject<ApiPoolPayoutSchemeConfig>()
                    : new ApiPoolPayoutSchemeConfig();

            if(payoutConfig.PayoutScheme != PayoutScheme.PPLNSBF && response.Pool.PaymentProcessing.PayoutSchemeConfig != null)
                response.Pool.PaymentProcessing.PayoutSchemeConfig.BlockFinderPercentage = null;
        }

        // pool effort since last block (if any)
        if(lastBlockTime.HasValue)
        {
            var shareMultiplier = poolInstance?.ShareMultiplier ?? 1d;
            var poolEffort = await cf.Run(con =>
                shareRepo.GetEffortBetweenCreatedAsync(con, poolCfg.Id, shareMultiplier, lastBlockTime.Value, clock.Now, ct));

            if(poolEffort.HasValue)
                response.Pool.PoolEffort = poolEffort.Value;
        }

        // top miners window
        var from = clock.Now.AddHours(-topMinersRange);
        response.Pool.TopMiners = (await cf.Run(con =>
                statsRepo.PagePoolMinersByHashrateAsync(con, poolCfg.Id, from, 0, 15, ct)))
            .Select(mapper.Map<MinerPerformanceStats>)
            .ToArray();

        return response;
    }


    [HttpGet("{poolId}/performance")]
    public async Task<GetPoolStatsResponse> GetPoolPerformanceAsync(string poolId,
        [FromQuery(Name = "r")] SampleRange range = SampleRange.Day,
        [FromQuery(Name = "i")] SampleInterval interval = SampleInterval.Hour)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // set range
        var end = clock.Now;
        DateTime start;

        switch(range)
        {
            case SampleRange.Day:
                start = end.AddDays(-1);
                break;

            case SampleRange.Month:
                start = end.AddDays(-30);
                break;

            default:
                throw new ApiException("invalid interval");
        }

        var stats = await cf.Run(con => statsRepo.GetPoolPerformanceBetweenAsync(con, pool.Id, interval, start, end, ct));

        var response = new GetPoolStatsResponse
        {
            Stats = stats.Select(mapper.Map<AggregatedPoolStats>).ToArray()
        };

        return response;
    }

    [HttpGet("{poolId}/miners")]
    public async Task<MinerPerformanceStats[]> PagePoolMinersAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] uint topMinersRange = 24)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // set range
        var end = clock.Now;
        var start = end.AddHours(-topMinersRange);

        var miners = (await cf.Run(con => statsRepo.PagePoolMinersByHashrateAsync(con, pool.Id, start, page, pageSize, ct)))
            .Select(mapper.Map<MinerPerformanceStats>)
            .ToArray();

        return miners;
    }

    [HttpGet("{poolId}/blocks")]
    public async Task<Responses.Block[]> PagePoolBlocksAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PagePoolBlocksV2Async(
    string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(pageSize < 1) pageSize = 1; // guard against division by zero

        var blockStates = state is { Length: > 0 }
            ? state
            : new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        // NOTE: this counts ALL blocks; for perfect parity with filters, expose a repo count with state filter.
        uint itemCount = await cf.Run(con => blocksRepo.GetPoolBlockCountAsync(con, poolId, ct));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var blocks = (await cf.Run(con => blocksRepo.PageBlocksAsync(con, pool.Id, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // Enrich info links
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;
        foreach(var block in blocks)
        {
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var baseUrl);

                if(!string.IsNullOrEmpty(baseUrl))
                {
                    if(baseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = baseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(baseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = baseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
    }

    [HttpGet("{poolId}/payments")]
    public async Task<Responses.Payment[]> PagePoolPaymentsAsync(
        string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, null, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return payments;
    }

    [HttpGet("/api/v2/pools/{poolId}/payments")]
    public async Task<PagedResultResponse<Responses.Payment[]>> PagePoolPaymentsV2Async(
    string poolId, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(pageSize < 1) pageSize = 1;

        uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, null, ct));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(con, pool.Id, null, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
    }

    [HttpGet("{poolId}/miners/{address}")]
    public async Task<Responses.MinerStats> GetMinerInfoAsync(
        string poolId, string address, [FromQuery] SampleRange perfMode = SampleRange.Day)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var statsResult = await cf.RunTx((con, tx) =>
            statsRepo.GetMinerStatsAsync(con, tx, pool.Id, address, ct), true, IsolationLevel.Serializable);

        Responses.MinerStats stats = null;

        if(statsResult != null)
        {
            stats = mapper.Map<Responses.MinerStats>(statsResult);

            // pre-multiply pending shares to cause less confusion with users
            if(pool.Template.Family == CoinFamily.Bitcoin)
                stats.PendingShares *= pool.Template.As<BitcoinTemplate>().ShareMultiplier;

            // optional fields
            if(statsResult.LastPayment != null)
            {
                // Set timestamp of last payment
                stats.LastPayment = statsResult.LastPayment.Created;

                // Compute info link
                var baseUrl = pool.Template.ExplorerTxLink;
                if(!string.IsNullOrEmpty(baseUrl))
                    stats.LastPaymentLink = string.Format(baseUrl, statsResult.LastPayment.TransactionConfirmationData);
            }

            var lastBlockTime = await cf.Run(con => blocksRepo.GetLastPoolBlockTimeAsync(con, pool.Id, ct));
            if(lastBlockTime.HasValue)
            {
                var startTime = lastBlockTime.Value;
                var minerEffort = await cf.Run(con => shareRepo.GetMinerEffortBetweenCreatedAsync(con, pool.Id, address, startTime, clock.Now, ct));
                if(minerEffort.HasValue)
                    stats.MinerEffort = minerEffort.Value;
            }

            stats.PerformanceSamples = await GetMinerPerformanceInternal(perfMode, pool, address, ct);

            // add total confirmed and pending blocks
            var totalConfirmedBlocks = await cf.Run(con => statsRepo.GetMinerTotalConfirmedBlocksAsync(con, pool.Id, address, ct));
            var totalPendingBlocks = await cf.Run(con => statsRepo.GetMinerTotalPendingBlocksAsync(con, pool.Id, address, ct));
            stats.TotalConfirmedBlocks = totalConfirmedBlocks;
            stats.TotalPendingBlocks = totalPendingBlocks;
        }

        return stats;
    }

    [HttpGet("{poolId}/miners/{address}/blocks")]
    public async Task<Responses.Block[]> PageMinerBlocksAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var blockStates = state is { Length: > 0 } ?
            state :
            new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        // enrich blocks
        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;

        foreach(var block in blocks)
        {
            // compute infoLink
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var blockInfobaseUrl);

                if(!string.IsNullOrEmpty(blockInfobaseUrl))
                {
                    if(blockInfobaseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(blockInfobaseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = blockInfobaseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return blocks;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/blocks")]
    public async Task<PagedResultResponse<Responses.Block[]>> PageMinerBlocksV2Async(
    string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15, [FromQuery] BlockStatus[] state = null)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(pageSize < 1) pageSize = 1;

        var blockStates = state is { Length: > 0 }
            ? state
            : new[] { BlockStatus.Confirmed, BlockStatus.Pending, BlockStatus.Orphaned };

        uint itemCount = await cf.Run(con => blocksRepo.GetMinerBlockCountAsync(con, poolId, address, ct));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var blocks = (await cf.Run(con => blocksRepo.PageMinerBlocksAsync(con, pool.Id, address, blockStates, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Block>)
            .ToArray();

        var blockInfobaseDict = pool.Template.ExplorerBlockLinks;
        foreach(var block in blocks)
        {
            if(blockInfobaseDict != null)
            {
                blockInfobaseDict.TryGetValue(!string.IsNullOrEmpty(block.Type) ? block.Type : "block", out var baseUrl);

                if(!string.IsNullOrEmpty(baseUrl))
                {
                    if(baseUrl.Contains(CoinMetaData.BlockHeightPH))
                        block.InfoLink = baseUrl.Replace(CoinMetaData.BlockHeightPH, block.BlockHeight.ToString(CultureInfo.InvariantCulture));
                    else if(baseUrl.Contains(CoinMetaData.BlockHashPH) && !string.IsNullOrEmpty(block.Hash))
                        block.InfoLink = baseUrl.Replace(CoinMetaData.BlockHashPH, block.Hash);
                }
            }
        }

        return new PagedResultResponse<Responses.Block[]>(blocks, itemCount, pageCount);
    }


    [HttpGet("{poolId}/miners/{address}/payments")]
    public async Task<Responses.Payment[]> PageMinerPaymentsAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        // enrich payments
        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            // compute transaction infoLink
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            // pool wallet link
            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return payments;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/payments")]
    public async Task<PagedResultResponse<Responses.Payment[]>> PageMinerPaymentsV2Async(
    string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(pageSize < 1) pageSize = 1;

        uint itemCount = await cf.Run(con => paymentsRepo.GetPaymentsCountAsync(con, poolId, address, ct));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var payments = (await cf.Run(con => paymentsRepo.PagePaymentsAsync(con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.Payment>)
            .ToArray();

        var txInfobaseUrl = pool.Template.ExplorerTxLink;
        var addressInfobaseUrl = pool.Template.ExplorerAccountLink;

        foreach(var payment in payments)
        {
            if(!string.IsNullOrEmpty(txInfobaseUrl))
                payment.TransactionInfoLink = string.Format(txInfobaseUrl, payment.TransactionConfirmationData);

            if(!string.IsNullOrEmpty(addressInfobaseUrl))
                payment.AddressInfoLink = string.Format(addressInfobaseUrl, payment.Address);
        }

        return new PagedResultResponse<Responses.Payment[]>(payments, itemCount, pageCount);
    }


    [HttpGet("{poolId}/miners/{address}/balancechanges")]
    public async Task<Responses.BalanceChange[]> PageMinerBalanceChangesAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.BalanceChange>)
            .ToArray();

        return balanceChanges;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/balancechanges")]
    public async Task<PagedResultResponse<Responses.BalanceChange[]>> PageMinerBalanceChangesV2Async(
    string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(pageSize < 1) pageSize = 1;

        uint itemCount = await cf.Run(con => paymentsRepo.GetBalanceChangesCountAsync(con, poolId, address));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var balanceChanges = (await cf.Run(con => paymentsRepo.PageBalanceChangesAsync(con, pool.Id, address, page, pageSize, ct)))
            .Select(mapper.Map<Responses.BalanceChange>)
            .ToArray();

        return new PagedResultResponse<Responses.BalanceChange[]>(balanceChanges, itemCount, pageCount);
    }


    [HttpGet("{poolId}/miners/{address}/earnings/daily")]
    public async Task<AmountByDate[]> PageMinerEarningsByDayAsync(
        string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(
                con, pool.Id, address, page, pageSize, ct)))
            .ToArray();

        return earnings;
    }

    [HttpGet("/api/v2/pools/{poolId}/miners/{address}/earnings/daily")]
    public async Task<PagedResultResponse<AmountByDate[]>> PageMinerEarningsByDayV2Async(
    string poolId, string address, [FromQuery] int page, [FromQuery] int pageSize = 15)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(pageSize < 1) pageSize = 1;

        uint itemCount = await cf.Run(con => paymentsRepo.GetMinerPaymentsByDayCountAsync(con, poolId, address));
        uint pageCount = (uint) Math.Ceiling(itemCount / (double) pageSize);

        var earnings = (await cf.Run(con => paymentsRepo.PageMinerPaymentsByDayAsync(con, pool.Id, address, page, pageSize, ct)))
            .ToArray();

        return new PagedResultResponse<AmountByDate[]>(earnings, itemCount, pageCount);
    }


    [HttpGet("{poolId}/miners/{address}/performance")]
    public async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceAsync(
        string poolId, string address, [FromQuery] SampleRange mode = SampleRange.Day)
    {
        var pool = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var result = await GetMinerPerformanceInternal(mode, pool, address, ct);

        return result;
    }

    [HttpGet("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> GetMinerSettingsAsync(string poolId, string address)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        var result = await cf.Run(con => minerRepo.GetSettingsAsync(con, null, pool.Id, address));

        if(result == null)
            throw new ApiException("No settings found", HttpStatusCode.NotFound);

        return mapper.Map<Responses.MinerSettings>(result);
    }

    [HttpPost("{poolId}/miners/{address}/settings")]
    public async Task<Responses.MinerSettings> SetMinerSettingsAsync(string poolId, string address,
        [FromBody] Requests.UpdateMinerSettingsRequest request, CancellationToken ct)
    {
        var pool = GetPool(poolId);

        if(string.IsNullOrEmpty(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.NotFound);

        if(pool.Template.Family == CoinFamily.Ethereum)
            address = address.ToLower();

        if(request?.Settings == null)
            throw new ApiException("Invalid or missing settings", HttpStatusCode.BadRequest);

        if(!IPAddress.TryParse(request.IpAddress, out var requestIp))
            throw new ApiException("Invalid IP address", HttpStatusCode.BadRequest);

        // fetch recent IPs
        var ips = await cf.Run(con => shareRepo.GetRecentyUsedIpAddressesAsync(con, null, poolId, address, ct));

        // any known ips?
        if(ips == null || ips.Length == 0)
            throw new ApiException("Address not recently used for mining", HttpStatusCode.NotFound);

        // match?
        if(!ips.Any(x => IPAddress.TryParse(x, out var ipAddress) && ipAddress.IsEqual(requestIp)))
            throw new ApiException("None of the recently used IP addresses matches the request", HttpStatusCode.Forbidden);

        // map settings
        var mapped = mapper.Map<Persistence.Model.MinerSettings>(request.Settings);

        // clamp limit
        if(pool.PaymentProcessing != null)
            mapped.PaymentThreshold = Math.Max(mapped.PaymentThreshold, pool.PaymentProcessing.MinimumPayment);

        mapped.PoolId = pool.Id;
        mapped.Address = address;

        // finally update the settings
        return await cf.RunTx(async (con, tx) =>
        {
            await minerRepo.UpdateSettingsAsync(con, tx, mapped);

            logger.Info(() => $"Updated settings for pool {pool.Id}, miner {address}");

            var result = await minerRepo.GetSettingsAsync(con, tx, mapped.PoolId, mapped.Address);
            return mapper.Map<Responses.MinerSettings>(result);
        });
    }

    #endregion // Actions

    private async Task<Responses.WorkerPerformanceStatsContainer[]> GetMinerPerformanceInternal(
        SampleRange mode, PoolConfig pool, string address, CancellationToken ct)
    {
        Persistence.Model.Projections.WorkerPerformanceStatsContainer[] stats = null;
        var end = clock.Now;
        DateTime start;

        switch(mode)
        {
            case SampleRange.Hour:
                end = end.AddSeconds(-end.Second);

                start = end.AddHours(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenThreeMinutelyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Day:
                // set range
                if(end.Minute < 30)
                    end = end.AddHours(-1);

                end = end.AddMinutes(-end.Minute);
                end = end.AddSeconds(-end.Second);

                start = end.AddDays(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenHourlyAsync(con, pool.Id, address, start, end, ct));
                break;

            case SampleRange.Month:
                if(end.Hour < 12)
                    end = end.AddDays(-1);

                end = end.Date;

                // set range
                start = end.AddMonths(-1);

                stats = await cf.Run(con => statsRepo.GetMinerPerformanceBetweenDailyAsync(con, pool.Id, address, start, end, ct));
                break;
        }

        // map
        var result = mapper.Map<Responses.WorkerPerformanceStatsContainer[]>(stats);
        return result;
    }
}

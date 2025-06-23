using System.Data;
using System.Data.Common;
using System.Net.Sockets;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Util;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments.PaymentSchemes;

/// <summary>
/// PPS payout scheme implementation
/// </summary>
// ReSharper disable once InconsistentNaming
public class PPSPaymentScheme : IPayoutScheme
{
    public PPSPaymentScheme(
        IConnectionFactory cf,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        IBlockRepository blockRepo)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(blockRepo);

        this.cf = cf;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;
        this.blockRepo = blockRepo;

        BuildFaultHandlingPolicy();
    }

    private readonly IBalanceRepository balanceRepo;
    private readonly IBlockRepository blockRepo;
    private readonly IConnectionFactory cf;
    private readonly IShareRepository shareRepo;
    private static readonly ILogger logger = LogManager.GetLogger("PPS Payment");

    private const int RetryCount = 4;
    private IAsyncPolicy shareReadFaultPolicy;

    private class Config
    {
        public decimal PayoutPerShare { get; set; } // Fixed PPS payout per valid share (fallback)
    }

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var payoutConfig = poolConfig.PaymentProcessing.PayoutSchemeConfig;

        // Fetch the latest mined block to determine network difficulty
        var latestBlock = await blockRepo.GetLatestBlockAsync(con, poolConfig.Id, ct);
        if (latestBlock == null)
        {
            logger.Warn(() => "No blocks found. PPS payout cannot be calculated.");
            return;
        }

        // Calculate PPS payout per share dynamically based on network difficulty
        var payoutPerShare = CalculatePayoutPerShare(latestBlock.Reward, latestBlock.NetworkDifficulty);

        // Use the config fallback if the dynamic calculation fails
        payoutPerShare = payoutPerShare > 0 ? payoutPerShare : payoutConfig?.ToObject<Config>()?.PayoutPerShare ?? 0m;

        if (payoutPerShare <= 0)
        {
            logger.Error(() => "PPS payout per share is not set correctly. Check pool configuration.");
            return;
        }

        // Retrieve shares
        var shares = new Dictionary<string, double>();
        var rewards = new Dictionary<string, decimal>();
        await CalculateRewardsAsync(pool, payoutHandler, payoutPerShare, shares, rewards, ct);

        // Update miner balances
        foreach (var address in rewards.Keys)
        {
            var amount = rewards[address];

            if (amount > 0)
            {
                logger.Info(() => $"Crediting {address} with {payoutHandler.FormatAmount(amount)} for {FormatUtil.FormatQuantity(shares[address])} shares.");
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"PPS Reward for shares contributed.");
            }
        }
    }

    private async Task CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, decimal payoutPerShare,
        Dictionary<string, double> shares, Dictionary<string, decimal> rewards, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var before = DateTime.UtcNow;
        var pageSize = 50000;
        var totalShares = 0.0m;

        while (!ct.IsCancellationRequested)
        {
            var page = await shareReadFaultPolicy.ExecuteAsync(() =>
                cf.Run(con => shareRepo.ReadRecentSharesAsync(con, poolConfig.Id, before, pageSize, ct)));

            foreach (var share in page)
            {
                var address = share.Miner;
                var shareDiffAdjusted = payoutHandler.AdjustShareDifficulty(share.Difficulty);

                if (!shares.ContainsKey(address))
                    shares[address] = shareDiffAdjusted;
                else
                    shares[address] += shareDiffAdjusted;

                totalShares += (decimal)shareDiffAdjusted;
            }

            if (page.Length < pageSize)
                break;

            before = page[^1].Created;
        }

        // Distribute rewards per share
        if (totalShares > 0)
        {
            foreach (var address in shares.Keys)
            {
                var minerReward = (decimal)shares[address] * payoutPerShare;

                if (minerReward > 0)
                    rewards[address] = minerReward;
            }
        }
        else
        {
            logger.Warn(() => "No valid shares found. Skipping PPS payout calculation.");
        }
    }

    private decimal CalculatePayoutPerShare(decimal blockReward, decimal networkDifficulty)
    {
        if (networkDifficulty <= 0)
        {
            logger.Warn(() => "Network difficulty is zero or negative. Using fallback PPS payout per share.");
            return 0m;
        }

        // PPS formula: Block reward divided by network difficulty
        return blockReward / networkDifficulty;
    }

    private void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<SocketException>()
            .Or<TimeoutException>()
            .RetryAsync(RetryCount, OnPolicyRetry);

        shareReadFaultPolicy = retry;
    }

    private static void OnPolicyRetry(Exception ex, int retry, object context)
    {
        logger.Warn(() => $"Retry {retry} due to {ex.Source}: {ex.GetType().Name} ({ex.Message})");
    }

    #endregion // IPayoutScheme
}
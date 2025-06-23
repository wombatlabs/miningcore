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
/// FPPS payout scheme implementation
/// </summary>
// ReSharper disable once InconsistentNaming
public class FPPSPaymentScheme : IPayoutScheme
{
    public FPPSPaymentScheme(
        IConnectionFactory cf,
        IShareRepository shareRepo,
        IBalanceRepository balanceRepo,
        IBlockRepository blockRepo) // Added blockRepo injection
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(blockRepo);

        this.cf = cf;
        this.shareRepo = shareRepo;
        this.balanceRepo = balanceRepo;
        this.blockRepo = blockRepo; // Assign injected block repository

        BuildFaultHandlingPolicy();
    }

    private readonly IBlockRepository blockRepo; // Newly injected block repository
    private readonly IBalanceRepository balanceRepo;
    private readonly IConnectionFactory cf;
    private readonly IShareRepository shareRepo;
    private static readonly ILogger logger = LogManager.GetLogger("FPPS Payment");

    private const int RetryCount = 4;
    private IAsyncPolicy shareReadFaultPolicy;

    private class Config
    {
        public decimal FeeShare { get; set; } // Percentage of transaction fees included in payouts
    }

    #region IPayoutScheme

    public async Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler, CancellationToken ct)
    {
        var poolConfig = pool.Config;
        var payoutConfig = poolConfig.PaymentProcessing.PayoutSchemeConfig;

        // Ensure feeShare is between 0 and 100
        var feeShare = payoutConfig?.ToObject<Config>()?.FeeShare ?? 100m;
        feeShare = Math.Clamp(feeShare, 0, 100);

        // Retrieve average block reward & transaction fees over the last 10 blocks
        var (averageBlockReward, averageTxFees) = await GetAverageRewardsAsync(con, poolConfig.Id, ct);

        // Calculate the total reward distributed to miners
        var totalReward = averageBlockReward + (averageTxFees * (feeShare / 100));

        // Retrieve share contributions
        var shares = new Dictionary<string, double>();
        var rewards = new Dictionary<string, decimal>();
        var totalShares = await CalculateRewardsAsync(pool, payoutHandler, totalReward, shares, rewards, ct);

        if (totalShares <= 0)
        {
            logger.Warn(() => "No valid shares found. Skipping FPPS payout calculation.");
            return;
        }

        // Update miner balances
        foreach (var address in rewards.Keys)
        {
            var amount = rewards[address];

            if (amount > 0)
            {
                logger.Info(() => $"Crediting {address} with {payoutHandler.FormatAmount(amount)} for {FormatUtil.FormatQuantity(shares[address])} shares.");
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"FPPS Reward for shares contributed.");
            }
        }
    }

    private async Task<(decimal blockReward, decimal txFees)> GetAverageRewardsAsync(IDbConnection con, string poolId, CancellationToken ct)
    {
        // Now using the injected blockRepo to fetch the last 10 blocks.
        var blocks = await cf.Run(con => blockRepo.GetLastBlocksAsync(con, poolId, 10, ct));

        if (blocks == null || blocks.Length == 0)
            return (6.25m, 0m); // Default block reward if no blocks found

        var totalBlockReward = blocks.Sum(b => b.Reward);
        var totalTxFees = blocks.Sum(b => b.RewardFees ?? 0m);

        return (totalBlockReward / blocks.Length, totalTxFees / blocks.Length);
    }

    private async Task<decimal> CalculateRewardsAsync(IMiningPool pool, IPayoutHandler payoutHandler, decimal totalReward,
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

        // Calculate miner rewards
        if (totalShares > 0)
        {
            var payoutPerShare = totalReward / totalShares;

            foreach (var address in shares.Keys)
            {
                var minerReward = (decimal)shares[address] * payoutPerShare;

                if (minerReward > 0)
                    rewards[address] = minerReward;
            }
        }

        return totalShares;
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
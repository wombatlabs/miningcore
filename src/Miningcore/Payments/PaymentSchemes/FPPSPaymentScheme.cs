using System.Data;
using Miningcore.Mining;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Extensions;
using Miningcore.Util;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments.PaymentSchemes
{
    public class FPPSPaymentScheme : IPayoutScheme
    {
        private readonly IBalanceRepository balanceRepo;
        private readonly IShareRepository shareRepo;
        private readonly IConnectionFactory cf;
        private static readonly ILogger logger = LogManager.GetLogger("FPPS Payment");
        private const int RetryCount = 4;
        private IAsyncPolicy shareReadFaultPolicy;

        public FPPSPaymentScheme(
            IConnectionFactory cf,
            IShareRepository shareRepo,
            IBalanceRepository balanceRepo)
        {
            Contract.RequiresNonNull(cf);
            Contract.RequiresNonNull(shareRepo);
            Contract.RequiresNonNull(balanceRepo);

            this.cf = cf;
            this.shareRepo = shareRepo;
            this.balanceRepo = balanceRepo;
            BuildFaultHandlingPolicy();
        }

        public async Task UpdateBalancesAsync(
            IDbConnection con, IDbTransaction tx,
            IMiningPool pool, IPayoutHandler payoutHandler,
            Block block, decimal blockReward, CancellationToken ct)
        {
            var poolConfig = pool.Config;
            var pageSize   = 100_000;
            var before     = block.Created;
            var inclusive  = true;

            var rewards = new Dictionary<string, decimal>();
            var shares  = new Dictionary<string, decimal>();

            while(true)
            {
                var page = await shareReadFaultPolicy.ExecuteAsync(() =>
                    cf.Run(db => shareRepo.ReadSharesBeforeAsync(db,
                        poolConfig.Id, before, inclusive, pageSize, ct)));

                if(page.Length == 0)
                    break;

                inclusive = false;
                foreach(var share in page)
                {
                    var miner        = share.Miner;
                    // cast to decimal so we stay in one domain
                    var adjustedDiff = (decimal)payoutHandler.AdjustShareDifficulty(share.Difficulty);
                    var networkDiff  = (decimal)share.NetworkDifficulty;

                    // for PPS, totalReward = blockReward
                    // for FPPS, blockReward already includes fees
                    var totalReward = blockReward;

                    var reward = adjustedDiff * totalReward / networkDiff;
                    if(reward <= 0)
                        continue;

                    rewards[miner] = rewards.GetValueOrDefault(miner) + reward;
                    shares[miner]  = shares.GetValueOrDefault(miner)  + adjustedDiff;
                }

                before = page[^1].Created;
                if(page.Length < pageSize)
                    break;
            }

            foreach(var (miner, amount) in rewards)
            {
                logger.Info(() =>
                    $"Crediting {miner} with {payoutHandler.FormatAmount(amount)} " +
                    $"for {FormatUtil.FormatQuantity((double)shares[miner])} shares");
                await balanceRepo.AddAmountAsync(
                    con, tx, poolConfig.Id, miner, amount,
                    $"Reward for {FormatUtil.FormatQuantity((double)shares[miner])} shares for block {block.BlockHeight}");
            }

            if(before > block.Created)
                await shareRepo.DeleteSharesBeforeAsync(
                    con, tx, poolConfig.Id, block.Created, ct);
        }

        private void BuildFaultHandlingPolicy()
        {
            shareReadFaultPolicy = Policy
                .Handle<Exception>()
                .RetryAsync(RetryCount, (ex, retry) =>
                    logger.Warn($"Retry {retry} due to {ex.GetType().Name}: {ex.Message}"));
        }
    }
}

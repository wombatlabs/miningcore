using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Blockchain.Conceal.StratumRequests;
using Miningcore.Blockchain.Conceal.StratumResponses;
using Miningcore.Configuration;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Conceal;

[CoinFamily(CoinFamily.Conceal)]
public class ConcealPool : PoolBase
{
    public ConcealPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    private long currentJobId;

    private ConcealJobManager manager;
    private string minerAlgo;

    private async Task OnLoginAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<ConcealWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var loginRequest = request.ParamsAs<ConcealLoginRequest>();
        if(string.IsNullOrEmpty(loginRequest?.Login))
            throw new StratumException(StratumError.MinusOne, "missing login");

        // miner.worker
        var split = loginRequest.Login.Split('.');
        context.Miner = split[0].Trim();
        context.Worker = split.Length > 1 ? split[1].Trim() : null;
        context.UserAgent = loginRequest.UserAgent?.Trim();

        var addressToValidate = context.Miner;

        // payment id after '#'
        var index = context.Miner.IndexOf('#');
        if(index != -1)
        {
            var paymentId = context.Miner.Substring(index + 1).Trim();

            if(!string.IsNullOrEmpty(paymentId) && paymentId.Length != ConcealConstants.PaymentIdHexLength)
                throw new StratumException(StratumError.MinusOne, "invalid payment id");

            addressToValidate = context.Miner.Substring(0, index).Trim();
            context.Miner = addressToValidate + PayoutConstants.PayoutInfoSeperator + paymentId;
        }

        // validates Adress
        var isValid = manager.ValidateAddress(addressToValidate);
        context.IsSubscribed = isValid;
        context.IsAuthorized = isValid;

        if(context.IsAuthorized)
        {
            // controls diff via password
            var passParts = loginRequest.Password?.Split(PasswordControlVarsSeparator);
            var staticDiff = GetStaticDiffFromPassparts(passParts);

            // Nicehash
            var nicehashDiff = await GetNicehashStaticMinDiff(context, addressToValidate, minerAlgo);
            if(nicehashDiff.HasValue)
                staticDiff = Math.Max(staticDiff ?? 0, nicehashDiff.Value);

            if(staticDiff.HasValue && staticDiff.Value > 0)
                context.VarDiff = null; // fixa diff
        }

        await SendLoginResponseAsync(connection, context, request.Id);
    }

    // Sends the login ack expected by miners (id + initial job), Nicehash-safe
    private async Task SendLoginResponseAsync(StratumConnection connection, ConcealWorkerContext context, object requestId)
    {
        // build initial job for this worker
        var job = CreateWorkerJob(connection);

        // minimal result shape most miners accept: { id, job, status }
        var result = new
        {
            id = connection.ConnectionId,
            job,
            status = "OK"
        };

        var response = new JsonRpcResponse<object>(result, requestId);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        await connection.RespondAsync(response);
    }


    private async Task OnGetJobAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<ConcealWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var getJobRequest = request.ParamsAs<ConcealGetJobRequest>();

        // validate worker
        if(connection.ConnectionId != getJobRequest?.WorkerId || !context.IsAuthorized)
            throw new StratumException(StratumError.MinusOne, "unauthorized");

        var job = CreateWorkerJob(connection);

        // Nicehash's stupid validator insists on "error" property present
        // in successful responses which is a violation of the JSON-RPC spec
        // [Respect the goddamn standards Nicehack :(]
        var response = new JsonRpcResponse<object>(job, request.Id);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        // respond
        await connection.RespondAsync(response);
    }

    private ConcealJobParams CreateWorkerJob(StratumConnection connection)
    {
        var context = connection.ContextAs<ConcealWorkerContext>();
        var job = new ConcealWorkerJob(NextJobId(), context.Difficulty);

        string blob = null;
        string target = null;

        try
        {
            // Prepare miner-specific job data (blob + target)
            manager.PrepareWorkerJob(job, out blob, out target);
        }
        catch(Exception ex)
        {
            logger.Error(ex, $"[{connection.ConnectionId}] Failed to prepare Conceal worker job");
            return null;
        }


        // Should never happen, but do not send partial jobs
        if(string.IsNullOrEmpty(blob) || string.IsNullOrEmpty(target))
        {
            logger.Warn(() => $"[{connection.ConnectionId}] Ignoring empty Conceal job (blob/target missing)");
            return null;
        }

        var result = new ConcealJobParams
        {
            JobId = job.Id,
            Blob = blob,
            Target = target,
            Height = job.Height
        };

        // Optional algo hint to miner (when available)
        if(!string.IsNullOrEmpty(minerAlgo))
            result.Algorithm = minerAlgo;

        // Update worker context with a small rolling window of recent jobs
        lock(context)
        {
            context.AddJob(job, 4);
        }

        return result;
    }


    private async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<ConcealWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            // authorized worker
            if(!context.IsAuthorized)
                throw new StratumException(StratumError.MinusOne, "unauthorized");

            // check age of submission (aged submissions are usually caused by high server load)
            var requestAge = clock.Now - tsRequest.Timestamp.UtcDateTime;

            if(requestAge > maxShareAge)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Dropping stale share submission request (server overloaded?)");
                return;
            }

            // check request
            var submitRequest = request.ParamsAs<ConcealSubmitShareRequest>();

            // validate worker
            if(connection.ConnectionId != submitRequest?.WorkerId)
                throw new StratumException(StratumError.MinusOne, "cheater");

            // recognize activity
            context.LastActivity = clock.Now;

            ConcealWorkerJob job;

            lock(context)
            {
                var jobId = submitRequest?.JobId;

                if((job = context.GetJob(jobId)) == null)
                    throw new StratumException(StratumError.MinusOne, "invalid jobid");
            }

            // dupe check
            if(!job.Submissions.TryAdd(submitRequest.Nonce, true))
                throw new StratumException(StratumError.MinusOne, "duplicate share");

            // submit
            var share = await manager.SubmitShareAsync(connection, submitRequest, job, ct);

            // Nicehash's stupid validator insists on "error" property present
            // in successful responses which is a violation of the JSON-RPC spec
            // [Respect the goddamn standards Nicehack :(]
            var response = new JsonRpcResponse<object>(new ConcealResponseBase(), request.Id);

            if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
            {
                response.Extra = new Dictionary<string, object>();
                response.Extra["error"] = null;
            }

            await connection.RespondAsync(response);

            // publish
            messageBus.SendMessage(share);

            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            logger.Info(() => $"[{connection.ConnectionId}] Share accepted: D={Math.Round(share.Difficulty, 3)}");

            // update pool stats
            if(share.IsBlockCandidate)
                poolStats.LastPoolBlockTime = clock.Now;

            // update client stats
            context.Stats.ValidShares++;

            await UpdateVarDiffAsync(connection, false, ct);
        }

        catch(StratumException ex)
        {
            // telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // update client stats
            context.Stats.InvalidShares++;
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {ex.Message} [{context.UserAgent}]");

            // banning
            ConsiderBan(connection, context, poolConfig.Banning);

            throw;
        }
    }

    private string NextJobId()
    {
        return Interlocked.Increment(ref currentJobId).ToString(CultureInfo.InvariantCulture);
    }

    private async Task OnNewJobAsync()
    {
        logger.Info(() => "Broadcasting jobs");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            // Build a fresh job for each connection
            var job = CreateWorkerJob(connection);

            // Defensive: skip if job could not be prepared
            if(job == null)
            {
                logger.Warn(() => $"[{connection.ConnectionId}] Skipped job notify (job is null)");
                return;
            }

            await connection.NotifyAsync(ConcealStratumMethods.JobNotify, job);
        }));
    }

    #region Overrides

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<ConcealJobManager>();
        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            minerAlgo = GetMinerAlgo();

            disposables.Add(manager.Blocks
                .Select(_ => Observable.FromAsync(() =>
                    Guard(OnNewJobAsync,
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // start with initial blocktemplate
            await manager.Blocks.Take(1).ToTask(ct);
        }

        else
        {
            // keep updating NetworkStats
            disposables.Add(manager.Blocks.Subscribe());
        }
    }

    private string GetMinerAlgo()
    {
        switch(manager.Coin.Hash)
        {
            case CryptonightHashType.CryptonightCCX:
                return $"cn-ccx";

            case CryptonightHashType.CryptonightGPU:
                return $"cn-gpu";
        }

        return null;
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new ConcealWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<ConcealWorkerContext>();

        try
        {
            switch(request.Method)
            {
                case ConcealStratumMethods.Login:
                    await OnLoginAsync(connection, tsRequest);
                    break;

                case ConcealStratumMethods.GetJob:
                    await OnGetJobAsync(connection, tsRequest);
                    break;

                case ConcealStratumMethods.Submit:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case ConcealStratumMethods.KeepAlive:
                    // recognize activity
                    context.LastActivity = clock.Now;

                    // For some reasons, we would never send a reply here :/
                    // But the official XMRig documentation is explicit, we should reply: https://xmrig.com/docs/extensions/keepalive
                    // XMRig is such a gift, i wish more mining pool operators value open-source, the same way the XMRig devs do

                    // Nicehash's stupid validator insists on "error" property present
                    // in successful responses which is a violation of the JSON-RPC spec
                    // [Respect the goddamn standards Nicehack :(]
                    var response = new JsonRpcResponse<object>(new ConcealKeepAliveResponse(), request.Id);

                    if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
                    {
                        response.Extra = new Dictionary<string, object>();
                        response.Extra["error"] = null;
                    }

                    await connection.RespondAsync(response);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");

                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }

        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        var result = shares / interval;
        return result;
    }

    public override double ShareMultiplier => 1;

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        // Only push a new job if the pending difficulty was actually applied
        if(connection.Context.ApplyPendingDifficulty())
        {
            var job = CreateWorkerJob(connection);
            if(job != null)
                await connection.NotifyAsync(ConcealStratumMethods.JobNotify, job);
            else
                logger.Warn(() => $"[{connection.ConnectionId}] VarDiff applied but job preparation failed");
        }
    }


    #endregion // Overrides
}
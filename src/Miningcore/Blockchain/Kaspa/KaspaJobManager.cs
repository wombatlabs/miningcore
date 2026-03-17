using System;
using static System.Array;
using System.Globalization;
using System.Net.Http;
using System.Numerics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autofac;
using Grpc.Core;
using Grpc.Net.Client;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Blockchain.Kaspa.Custom.Astrix;
using Miningcore.Blockchain.Kaspa.Custom.Karlsencoin;
using Miningcore.Blockchain.Kaspa.Custom.KaspaHeavy;
using Miningcore.Blockchain.Kaspa.Custom.Pyrin;
using Miningcore.Blockchain.Kaspa.Custom.Spectre;
using NLog;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public class KaspaJobManager : JobManagerBase<KaspaJob>
{
    public KaspaJobManager(
        IComponentContext ctx,
        IMessageBus messageBus,
        IMasterClock clock,
        IExtraNonceProvider extraNonceProvider)
        : base(ctx, messageBus)
    {
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }

    private DaemonEndpointConfig[] daemonEndpoints;
    private KaspaCoinTemplate coin;
    private kaspad.KaspadRPC.KaspadRPCClient rpc;
    private string network;
    private readonly IExtraNonceProvider extraNonceProvider;
    private readonly IMasterClock clock;
    private KaspaPoolConfigExtra extraPoolConfig;
    private KaspaPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    protected int maxActiveJobs;
    protected string extraData;
    protected IHashAlgorithm customBlockHeaderHasher;
    protected IHashAlgorithm customCoinbaseHasher;
    protected IHashAlgorithm customShareHasher;

    protected IObservable<kaspad.RpcBlock> KaspaSubscribeNewBlockTemplate(CancellationToken ct, object payload = null, JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        return Observable.Defer(() => Observable.Create<kaspad.RpcBlock>(obs =>
        {
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            _ = Task.Run(async () =>
            {
                using(cts)
                {
                retry_subscription:
                    var stream = rpc.MessageStream(null, null, cts.Token);

                    var reqSubscribe = new kaspad.KaspadMessage
                    {
                        NotifyNewBlockTemplateRequest = new kaspad.NotifyNewBlockTemplateRequestMessage()
                    };
                    var reqTemplate = new kaspad.KaspadMessage
                    {
                        GetBlockTemplateRequest = new kaspad.GetBlockTemplateRequestMessage
                        {
                            PayAddress = poolConfig.Address,
                            ExtraData = extraData,
                        }
                    };

                    logger.Debug(() => "Sending NotifyNewBlockTemplateRequest");

                    try
                    {
                        await stream.RequestStream.WriteAsync(reqSubscribe, cts.Token);
                    }
                    catch(Exception ex)
                    {
                        logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while subscribing to kaspad \"NewBlockTemplate\" notifications");
                        try { await stream.RequestStream.CompleteAsync(); } catch { /* ignore */ }
                        if(!cts.IsCancellationRequested)
                        {
                            logger.Error(() => "Reconnecting in 10s");
                            await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                            goto retry_subscription;
                        }
                        return;
                    }

                    while(!cts.IsCancellationRequested)
                    {
                    retry_template:
                        try
                        {
                            await stream.RequestStream.WriteAsync(reqTemplate, cts.Token);
                            await foreach(var resp in stream.ResponseStream.ReadAllAsync(cts.Token))
                            {
                                var err = resp.GetBlockTemplateResponse?.Error?.Message;
                                if(!string.IsNullOrEmpty(err))
                                    logger.Warn(() => err);

                                var block = resp.GetBlockTemplateResponse?.Block;
                                if(block != null)
                                {
                                    logger.Debug(() => $"DaaScore (BlockHeight): {block.Header?.DaaScore}");
                                    obs.OnNext(block);
                                    break;
                                }
                            }
                        }
                        catch(NullReferenceException)
                        {
                            logger.Info(() => "Waiting for `NewBlockTemplate` data...");
                            goto retry_template;
                        }
                        catch(Exception ex)
                        {
                            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while streaming kaspad \"NewBlockTemplate\" notifications");
                            try { await stream.RequestStream.CompleteAsync(); } catch { /* ignore */ }
                            if(!cts.IsCancellationRequested)
                            {
                                logger.Error(() => "Reconnecting in 10s");
                                await Task.Delay(TimeSpan.FromSeconds(10), cts.Token);
                                goto retry_subscription;
                            }
                            return;
                        }
                    }
                }
            }, cts.Token);

            return Disposable.Create(() => cts.Cancel());
        }));
    }


    private void SetupJobUpdates(CancellationToken ct)
    {
        var blockFound = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(string Via, kaspad.RpcBlock Data)>>
        {
            blockFound.Select(_ => (JobRefreshBy.BlockFound, (kaspad.RpcBlock) null))
        };

        // stream of NewBlockTemplate
        var getWorkKaspad = KaspaSubscribeNewBlockTemplate(ct)
            .Publish()
            .RefCount();

        triggers.Add(getWorkKaspad
            .Select(blockTemplate => (JobRefreshBy.BlockTemplateStream, blockTemplate))
            .Publish()
            .RefCount());

        // initial job 
        triggers.Add(Observable.Interval(TimeSpan.FromMilliseconds(1000))
            .Select(_ => (JobRefreshBy.Initial, (kaspad.RpcBlock) null))
            .TakeWhile(_ => !hasInitialBlockTemplate));

        Jobs = triggers.Merge()
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Via, x.Data)))
            .Concat()
            .Where(x => x)
            .Do(x =>
            {
                if(x)
                    hasInitialBlockTemplate = true;
            })
            .Select(x => GetJobParamsForStratum())
            .Publish()
            .RefCount();
    }

    private KaspaJob CreateJob(ulong blockHeight)
    {
        switch(coin.Symbol)
        {
            case "AIX":
                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not CShake256)
                    customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

                if(customShareHasher is not CShake256)
                    customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new AstrixJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

            case "CAS":
            case "HTN":
                if(customBlockHeaderHasher is not Blake3)
                {
                    string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
                    byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
                    customBlockHeaderHasher = new Blake3(hashBytes);
                }

                if(customCoinbaseHasher is not Blake3)
                    customCoinbaseHasher = new Blake3();

                if(customShareHasher is not Blake3)
                    customShareHasher = new Blake3();

                return new PyrinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

            case "KAS":
                // KASPA vanilla → HeavyHash on header
                var heavy = new HeavyHash();
                return new KaspaHeavyJob(heavy, heavy);

            case "KLS":
                var karlsenNetwork = network.ToLower();

                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not Blake3)
                    customCoinbaseHasher = new Blake3();

                if((karlsenNetwork == "testnet" && blockHeight >= KarlsencoinConstants.FishHashPlusForkHeightTestnet) || (karlsenNetwork == "mainnet" && blockHeight >= KarlsencoinConstants.FishHashPlusForkHeightMainnet))
                {
                    logger.Debug(() => $"fishHashPlusHardFork activated");

                    if(customShareHasher is not FishHashKarlsen)
                        customShareHasher = new FishHashKarlsen(FishHash.FishHashKernelPlus);
                    else if(customShareHasher is FishHashKarlsen fishHashKarlsenAlgo)
                    {
                        if(fishHashKarlsenAlgo.fishHashKernel != FishHash.FishHashKernelPlus)
                            customShareHasher = new FishHashKarlsen(FishHash.FishHashKernelPlus);
                    }

                }
                else if(karlsenNetwork == "testnet" && blockHeight >= KarlsencoinConstants.FishHashForkHeightTestnet)
                {
                    logger.Debug(() => $"fishHashHardFork activated");

                    if(customShareHasher is not FishHashKarlsen)
                        customShareHasher = new FishHashKarlsen();
                }
                else
                    if(customShareHasher is not CShake256)
                    customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new KarlsencoinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

            case "CSS":
            case "NTL":
            case "NXL":
            case "PUG":
                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not Blake3)
                    customCoinbaseHasher = new Blake3();

                if(customShareHasher is not CShake256)
                    customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new KarlsencoinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

            case "PYI":
                if(blockHeight >= PyrinConstants.Blake3ForkHeight)
                {
                    logger.Debug(() => $"blake3HardFork activated");

                    if(customBlockHeaderHasher is not Blake3)
                    {
                        string coinbaseBlockHash = KaspaConstants.CoinbaseBlockHash;
                        byte[] hashBytes = Encoding.UTF8.GetBytes(coinbaseBlockHash.PadRight(32, '\0')).Take(32).ToArray();
                        customBlockHeaderHasher = new Blake3(hashBytes);
                    }

                    if(customCoinbaseHasher is not Blake3)
                        customCoinbaseHasher = new Blake3();

                    if(customShareHasher is not Blake3)
                        customShareHasher = new Blake3();
                }
                else
                {
                    if(customBlockHeaderHasher is not Blake2b)
                        customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                    if(customCoinbaseHasher is not CShake256)
                        customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

                    if(customShareHasher is not CShake256)
                        customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));
                }

                return new PyrinJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);

            case "SPR":
                if(customBlockHeaderHasher is not Blake2b)
                    customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

                if(customCoinbaseHasher is not CShake256)
                    customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

                if(customShareHasher is not CShake256)
                    customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

                return new SpectreJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
        }

        if(customBlockHeaderHasher is not Blake2b)
            customBlockHeaderHasher = new Blake2b(Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseBlockHash));

        if(customCoinbaseHasher is not CShake256)
            customCoinbaseHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseProofOfWorkHash));

        if(customShareHasher is not CShake256)
            customShareHasher = new CShake256(null, Encoding.UTF8.GetBytes(KaspaConstants.CoinbaseHeavyHash));

        return new KaspaJob(customBlockHeaderHasher, customCoinbaseHasher, customShareHasher);
    }

    private async Task<bool> UpdateJob(CancellationToken ct, string via = null, kaspad.RpcBlock blockTemplate = null)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        return await Task.Run(() =>
        {
            using(cts)
            {
                try
                {
                    if(blockTemplate == null)
                        return false;

                    var job = currentJob;

                    var isNew = (job == null || job.BlockTemplate?.Header.DaaScore < blockTemplate.Header.DaaScore);

                    if(isNew)
                        messageBus.NotifyChainHeight(poolConfig.Id, blockTemplate.Header.DaaScore, poolConfig.Template);

                    if(isNew)
                    {
                        job = CreateJob(blockTemplate.Header.DaaScore);

                        job.Init(blockTemplate, NextJobId("D"), ShareMultiplier);

                        logger.Debug(() => $"blockTargetValue: {job.blockTargetValue}");
                        logger.Debug(() => $"Difficulty: {job.Difficulty}");

                        if(via != null)
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore} [{via}]");
                        else
                            logger.Info(() => $"Detected new block {job.BlockTemplate.Header.DaaScore}");

                        // update stats
                        if(job.BlockTemplate.Header.DaaScore > BlockchainStats.BlockHeight)
                        {
                            BlockchainStats.LastNetworkBlockTime = clock.Now;
                            BlockchainStats.BlockHeight = job.BlockTemplate.Header.DaaScore;
                            BlockchainStats.NetworkDifficulty = job.Difficulty;
                        }

                        currentJob = job;
                    }
                    else
                    {
                        if(via != null)
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                        else
                            logger.Debug(() => $"Template update {job.BlockTemplate.Header.DaaScore}");
                    }

                    return isNew;
                }

                catch(OperationCanceledException)
                {
                    // ignore
                }

                catch(Exception ex)
                {
                    logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating new job");
                }

                return false;
            }
        }, cts.Token);
    }

    private async Task UpdateNetworkStatsAsync(CancellationToken ct)
    {
        try
        {
            var stream = rpc.MessageStream(null, null, ct);

            var request = new kaspad.KaspadMessage();
            request.EstimateNetworkHashesPerSecondRequest = new kaspad.EstimateNetworkHashesPerSecondRequestMessage
            {
                WindowSize = 1000,
            };
            await stream.RequestStream.WriteAsync(request);
            await foreach(var infoHashrate in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(string.IsNullOrEmpty(infoHashrate.EstimateNetworkHashesPerSecondResponse.Error?.Message))
                    BlockchainStats.NetworkHashrate = (double) infoHashrate.EstimateNetworkHashesPerSecondResponse.NetworkHashesPerSecond;

                break;
            }

            request = new kaspad.KaspadMessage();
            request.GetConnectedPeerInfoRequest = new kaspad.GetConnectedPeerInfoRequestMessage();
            await stream.RequestStream.WriteAsync(request);
            await foreach(var info in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(string.IsNullOrEmpty(info.GetConnectedPeerInfoResponse.Error?.Message))
                    BlockchainStats.ConnectedPeers = info.GetConnectedPeerInfoResponse.Infos.Count;

                break;
            }
            await stream.RequestStream.CompleteAsync();
        }

        catch(Exception ex)
        {
            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while updating network stats");
        }
    }

    private async Task ShowDaemonSyncProgressAsync(CancellationToken ct)
    {
        var stream = rpc.MessageStream(null, null, ct);

        var request = new kaspad.KaspadMessage();
        request.GetInfoRequest = new kaspad.GetInfoRequestMessage();
        await Guard(() => stream.RequestStream.WriteAsync(request),
            ex => logger.Debug(ex));
        await foreach(var info in stream.ResponseStream.ReadAllAsync(ct))
        {
            if(!string.IsNullOrEmpty(info.GetInfoResponse.Error?.Message))
                logger.Debug(info.GetInfoResponse.Error?.Message);

            if(info.GetInfoResponse.IsSynced != true && info.GetInfoResponse.IsUtxoIndexed != true)
                logger.Info(() => $"Daemon is downloading headers ...");

            break;
        }
        await stream.RequestStream.CompleteAsync();
    }

    private async Task<bool> SubmitBlockAsync(CancellationToken ct, kaspad.RpcBlock block, object payload = null,
        JsonSerializerSettings payloadJsonSerializerSettings = null)
    {
        Contract.RequiresNonNull(block);

        bool succeed = false;

        try
        {
            var stream = rpc.MessageStream(null, null, ct);

            var request = new kaspad.KaspadMessage();
            request.SubmitBlockRequest = new kaspad.SubmitBlockRequestMessage
            {
                Block = block,
                AllowNonDAABlocks = false,
            };
            await stream.RequestStream.WriteAsync(request);
            await foreach(var response in stream.ResponseStream.ReadAllAsync(ct))
            {
                if(!string.IsNullOrEmpty(response.SubmitBlockResponse.Error?.Message))
                {
                    logger.Warn(() => $"Block submission failed: {response.SubmitBlockResponse.Error?.Message} [{response.SubmitBlockResponse?.RejectReason.ToString()}]");
                    messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id}: {response.SubmitBlockResponse.Error?.Message} [{response.SubmitBlockResponse?.RejectReason.ToString()}]"));
                }
                else
                    succeed = true;

                break;
            }
            await stream.RequestStream.CompleteAsync();
        }

        catch(Exception ex)
        {
            logger.Error(() => $"{ex.GetType().Name} '{ex.Message}' while submitting block");
            messageBus.SendMessage(new AdminNotification("Block submission failed", $"Pool {poolConfig.Id} failed to submit block"));
        }

        return succeed;
    }

    private object[] GetJobParamsForStratum()
    {
        var job = currentJob;
        return job?.GetJobParams();
    }

    public override KaspaJob GetJobForStratum()
    {
        var job = currentJob;
        return job;
    }

    #region API-Surface

    public IObservable<object[]> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();
    public string Network => network;

    public KaspaCoinTemplate Coin => coin;

    public object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<KaspaWorkerContext>();
        var extraNonce1Size = GetExtraNonce1Size();

        // assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            KaspaConstants.ExtranoncePlaceHolderLength - extraNonce1Size,
        };

        return responseData;
    }

    public int GetExtraNonce1Size()
    {
        return extraPoolConfig?.ExtraNonce1Size ?? 2;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<KaspaWorkerContext>();

        var jobId = submitParams[1] as string;
        var nonce = submitParams[2] as string;

        KaspaJob job;

        lock(context)
        {
            job = context.GetJob(jobId);

            if(job == null)
            {
                // hack for ASICs sending wrong jobIds (IceRiver/Bitmain)
                if(ValidateIsGodMiner(context.UserAgent) || ValidateIsIceRiverMiner(context.UserAgent))
                    job = context.validJobs.ToArray().FirstOrDefault(x => Int64.Parse(x.JobId) < Int64.Parse(jobId));
            }

            if(job == null)
                logger.Warn(() => $"[{context.Miner}] => jobId: {jobId} - Last known job: {context.validJobs.ToArray().FirstOrDefault()?.JobId}");
        }

        if(job == null)
            throw new StratumException(StratumError.JobNotFound, "job not found");

        // validate & process
        var share = job.ProcessShare(worker, nonce);

        // enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = clusterConfig.ClusterName;
        share.Created = clock.Now;

        // block candidate → submit
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting block {share.BlockHeight} [{share.BlockHash}]");

            var acceptResponse = await SubmitBlockAsync(ct, job.BlockTemplate);

            share.IsBlockCandidate = acceptResponse;

            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                share.TransactionConfirmationData = nonce;
            }

            else
            {
                share.TransactionConfirmationData = null;
            }
        }

        return share;
    }

    public bool ValidateIsLargeJob(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        if(ValidateIsBzMiner(userAgent))
            return true;

        if(ValidateIsIceRiverMiner(userAgent))
            return true;

        if(ValidateIsGoldShell(userAgent))
            return true;

        return false;
    }

    public bool ValidateIsBzMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        MatchCollection matchesUserAgentBzMiner = KaspaConstants.RegexUserAgentBzMiner.Matches(userAgent);
        return (matchesUserAgentBzMiner.Count > 0);
    }

    public bool ValidateIsGodMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        MatchCollection matchesUserAgentGodMiner = KaspaConstants.RegexUserAgentGodMiner.Matches(userAgent);
        return (matchesUserAgentGodMiner.Count > 0);
    }

    public bool ValidateIsIceRiverMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        MatchCollection matchesUserAgentIceRiverMiner = KaspaConstants.RegexUserAgentIceRiverMiner.Matches(userAgent);
        return (matchesUserAgentIceRiverMiner.Count > 0);
    }

    public bool ValidateIsGoldShell(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        MatchCollection matchesUserAgentGoldShell = KaspaConstants.RegexUserAgentGoldShell.Matches(userAgent);
        return (matchesUserAgentGoldShell.Count > 0);
    }

    public bool ValidateIsTNNMiner(string userAgent)
    {
        if(string.IsNullOrEmpty(userAgent))
            return false;

        MatchCollection matchesUserAgentTNNMiner = KaspaConstants.RegexUserAgentTNNMiner.Matches(userAgent);
        return (matchesUserAgentTNNMiner.Count > 0);
    }

    public double ShareMultiplier => coin.ShareMultiplier;

    #endregion // API-Surface

    #region Overrides

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        if(string.IsNullOrEmpty(poolConfig.Address))
            throw new PoolStartupException("Pool address is not configured", poolConfig.Id);

        var stream = rpc.MessageStream(null, null, ct);
        try
        {
            var request = new kaspad.KaspadMessage
            {
                GetCurrentNetworkRequest = new kaspad.GetCurrentNetworkRequestMessage()
            };

            await Guard(() => stream.RequestStream.WriteAsync(request),
                ex => throw new PoolStartupException($"Error writing GetCurrentNetworkRequest: {ex.Message}", poolConfig.Id));

            await foreach(var msg in stream.ResponseStream.ReadAllAsync(ct))
            {
                var err = msg.GetCurrentNetworkResponse?.Error?.Message;
                if(!string.IsNullOrEmpty(err))
                    throw new PoolStartupException($"Daemon reports: {err}", poolConfig.Id);

                var net = msg.GetCurrentNetworkResponse?.CurrentNetwork;
                if(string.IsNullOrEmpty(net))
                    throw new PoolStartupException("Daemon did not return a network name", poolConfig.Id);

                network = net;
                break;
            }
        }
        finally
        {
            await stream.RequestStream.CompleteAsync();
        }

        var (addrInfo, addrErr) = KaspaUtils.ValidateAddress(poolConfig.Address, network, coin);
        if(addrErr != null)
            throw new PoolStartupException($"Pool address {poolConfig.Address} invalid for network [{network}]: {addrErr}", poolConfig.Id);
        logger.Info(() => $"Pool address: {poolConfig.Address} => {KaspaConstants.KaspaAddressType[addrInfo.KaspaAddress.Version()]}");

        BlockchainStats.NetworkType = network;
        BlockchainStats.RewardType = "POW";

        stream = rpc.MessageStream(null, null, ct);
        try
        {
            var request = new kaspad.KaspadMessage
            {
                GetInfoRequest = new kaspad.GetInfoRequestMessage()
            };

            await Guard(() => stream.RequestStream.WriteAsync(request),
                ex => throw new PoolStartupException($"Error writing GetInfoRequest: {ex.Message}", poolConfig.Id));

            await foreach(var msg in stream.ResponseStream.ReadAllAsync(ct))
            {
                var err = msg.GetInfoResponse?.Error?.Message;
                if(!string.IsNullOrEmpty(err))
                    throw new PoolStartupException($"Daemon reports: {err}", poolConfig.Id);

                if(msg.GetInfoResponse?.IsUtxoIndexed != true)
                    throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);

                if(msg.GetInfoResponse?.ServerVersion != null)
                    BlockchainStats.NodeVersion = (string) msg.GetInfoResponse.ServerVersion;

                break;
            }
        }
        finally
        {
            await stream.RequestStream.CompleteAsync();
        }

        await UpdateNetworkStatsAsync(ct);

        Observable.Interval(TimeSpan.FromMinutes(1))
            .Select(_ => Observable.FromAsync(() =>
                Guard(() => UpdateNetworkStatsAsync(ct), ex => logger.Error(ex))))
            .Concat()
            .Subscribe();

        SetupJobUpdates(ct);
    }



    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<KaspaCoinTemplate>();

        extraPoolConfig = pc.Extra.SafeExtensionDataAs<KaspaPoolConfigExtra>();
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<KaspaPaymentProcessingConfigExtra>();

        maxActiveJobs = extraPoolConfig?.MaxActiveJobs ?? 8;
        extraData = extraPoolConfig?.ExtraData ?? "";

        daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .ToArray();

        base.Configure(pc, cc);
    }

    protected override void ConfigureDaemons()
    {
        logger.Debug(() => $"ProtobufDaemonRpcServiceName: {extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName}");
        rpc = KaspaClientFactory.CreateKaspadRPCClient(
            daemonEndpoints,
            extraPoolConfig?.ProtobufDaemonRpcServiceName ?? KaspaConstants.ProtobufDaemonRpcServiceName);
    }


    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        var stream = rpc.MessageStream(null, null, ct);
        try
        {
            var request = new kaspad.KaspadMessage { GetInfoRequest = new kaspad.GetInfoRequestMessage() };
            await Guard(() => stream.RequestStream.WriteAsync(request), ex => logger.Debug(ex));

            await foreach(var info in stream.ResponseStream.ReadAllAsync(ct))
            {
                var err = info.GetInfoResponse?.Error?.Message;
                if(!string.IsNullOrEmpty(err))
                {
                    logger.Debug(err);
                    return false;
                }

                if(info.GetInfoResponse?.IsUtxoIndexed != true)
                    throw new PoolStartupException("UTXO index is disabled", poolConfig.Id);

                if(info.GetInfoResponse?.ServerVersion != null)
                    BlockchainStats.NodeVersion = (string) info.GetInfoResponse.ServerVersion;

                return true;
            }

            return false;
        }
        finally
        {
            await stream.RequestStream.CompleteAsync();
        }
    }


    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        var stream = rpc.MessageStream(null, null, ct);
        try
        {
            var request = new kaspad.KaspadMessage
            {
                GetConnectedPeerInfoRequest = new kaspad.GetConnectedPeerInfoRequestMessage()
            };
            await Guard(() => stream.RequestStream.WriteAsync(request), ex => logger.Debug(ex));

            await foreach(var info in stream.ResponseStream.ReadAllAsync(ct))
            {
                var err = info.GetConnectedPeerInfoResponse?.Error?.Message;
                if(!string.IsNullOrEmpty(err))
                {
                    logger.Debug(err);
                    return false;
                }

                var peers = info.GetConnectedPeerInfoResponse?.Infos?.Count ?? 0;
                return peers > 0;
            }

            return false;
        }
        finally
        {
            await stream.RequestStream.CompleteAsync();
        }
    }


    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var notified = false;

        do
        {
            bool isSynced = false;

            var stream = rpc.MessageStream(null, null, ct);
            try
            {
                var request = new kaspad.KaspadMessage { GetInfoRequest = new kaspad.GetInfoRequestMessage() };
                await Guard(() => stream.RequestStream.WriteAsync(request), ex => logger.Debug(ex));

                await foreach(var info in stream.ResponseStream.ReadAllAsync(ct))
                {
                    var err = info.GetInfoResponse?.Error?.Message;
                    if(!string.IsNullOrEmpty(err))
                        logger.Debug(err);

                    isSynced = (info.GetInfoResponse?.IsSynced == true && info.GetInfoResponse?.IsUtxoIndexed == true);
                    break;
                }
            }
            finally
            {
                await stream.RequestStream.CompleteAsync();
            }

            if(isSynced)
            {
                logger.Info(() => "Daemon is synced with blockchain");
                break;
            }

            if(!notified)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                notified = true;
            }

            await ShowDaemonSyncProgressAsync(ct);
        }
        while(await timer.WaitForNextTickAsync(ct));
    }

    #endregion // Overrides
}

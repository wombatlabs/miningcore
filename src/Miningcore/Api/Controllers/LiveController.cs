// Miningcore/src/Miningcore/Api/Controllers/LiveController.cs
using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Miningcore.Api.Responses.Live;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Live;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Miningcore.Mining;
using Miningcore.Blockchain;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using MinerStats = Miningcore.Persistence.Model.Projections.MinerStats;

namespace Miningcore.Api.Controllers;

[ApiController]
[Route("api/live")]
public class LiveController : ControllerBase
{
    private readonly ClusterConfig clusterConfig;
    private readonly IConnectionFactory cf;
    private readonly IStatsRepository statsRepo;
    private readonly IMasterClock clock;
    private readonly MiningPoolRegistry poolRegistry;

    // ---- Live defaults & safety limits ----
    private const int DefaultWindowSec = 600;   // 10 minutes
    private const int MinWindowSec = 1;
    private const int MaxWindowSec = 1800;      // 30 minutes

    private const int DefaultLimit = 100;
    private const int MaxLimit = 500;

    private const int DefaultPageSize = 50;

    // Floor to avoid stupid spikes on tiny ages
    private const int MinEffectiveWindowSec = 30;

    public LiveController(
        ClusterConfig clusterConfig,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMasterClock clock,
        MiningPoolRegistry poolRegistry)
    {
        this.clusterConfig = clusterConfig;
        this.cf = cf;
        this.statsRepo = statsRepo;
        this.clock = clock;
        this.poolRegistry = poolRegistry;
    }

    // =========================
    // Helpers & shared logic
    // =========================

    private static bool IsEquihash(CoinFamily family) =>
        family == CoinFamily.Equihash ||
        family.ToString().Contains("Equihash", StringComparison.OrdinalIgnoreCase);

    private const double Diff1Hash = 4294967296d; // 2^32

    private static string ResolveUnit(CoinFamily family) =>
        IsEquihash(family) ? "Sol/s" : "H/s";

    private static ulong ToU64(long? v) =>
    v.HasValue && v.Value > 0 ? (ulong)v.Value : 0UL;

    private PoolConfig GetPool(string poolId)
    {
        var pool = clusterConfig.Pools?.FirstOrDefault(x =>
            string.Equals(x.Id, poolId, StringComparison.OrdinalIgnoreCase));

        if (pool == null)
            throw new ApiException($"Pool '{poolId}' not found", HttpStatusCode.NotFound);

        return pool;
    }

    private IMiningPool TryGetPoolInstance(string poolId) => poolRegistry.Get(poolId);

    private double DiffToHashrate(PoolConfig poolCfg, double diffSum, int windowSec, IMiningPool poolInst)
    {
        var perSec = diffSum / Math.Max(1d, windowSec);

        // Prefer pool-specific conversion if available
        if (poolInst != null)
            return poolInst.HashrateFromShares(diffSum, windowSec);

        // Fallback to classic Miningcore approach
        return IsEquihash(poolCfg.Template.Family) ? perSec : perSec * Diff1Hash;
    }

    private static object MapCoinMeta(PoolConfig poolCfg)
    {
        var t = poolCfg.Template;

        // pick a single "best" block link template for convenience
        string explorerBlockLink = null;
        if (t.ExplorerBlockLinks != null && t.ExplorerBlockLinks.Count > 0)
        {
            // prefer "block", else first available
            if (!t.ExplorerBlockLinks.TryGetValue("block", out explorerBlockLink))
                explorerBlockLink = t.ExplorerBlockLinks.Values.FirstOrDefault();
        }

        return new
        {
            name = t.Name ?? t.Symbol,
            symbol = t.Symbol,
            family = t.Family.ToString(),

            website = t.Website,
            market = t.Market,
            twitter = t.Twitter,
            telegram = t.Telegram,
            discord = t.Discord,

            // payments
            explorerTxLink = t.ExplorerTxLink,
            explorerAccountLink = t.ExplorerAccountLink,

            explorerBlockLink = explorerBlockLink,

            explorerBlockLinks = t.ExplorerBlockLinks
        };
    }

    private static bool IsOnlineFromLast(long lastSeenSec, int windowSec)
    {
        if (lastSeenSec <= 0)
            return false;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var grace = Math.Max(windowSec, Live.LiveHashrateState.OnlineGraceSec);

        return (now - lastSeenSec) <= grace;
    }

    private static object MapPoolStatic(PoolConfig poolCfg)
    {
        // Ports -> ordered array
        var ports = (poolCfg.Ports ?? new Dictionary<int, PoolEndpoint>())
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var p = kv.Value;
                var vd = p.VarDiff ?? new VarDiffConfig();

                return new
                {
                    port = kv.Key,
                    diff = p.Difficulty,
                    tls = p.Tls,
                    vardiff = new
                    {
                        min = vd.MinDiff,
                        max = vd.MaxDiff,
                        target = vd.TargetTime,
                        retargetSeconds = vd.RetargetTime,
                        delta = vd.VariancePercent
                    }
                };
            })
            .ToArray();

        var pay = poolCfg.PaymentProcessing;
        var feePercent = poolCfg.RewardRecipients != null
            ? (float)poolCfg.RewardRecipients.Sum(x => x.Percentage)
            : 0f;

        return new
        {
            id = poolCfg.Id,
            enabled = poolCfg.Enabled,
            feePercent = feePercent,
            payout = pay == null ? null : new
            {
                scheme = pay.PayoutScheme.ToString(),
                minimumPayment = pay.MinimumPayment,
                recipients = (poolCfg.RewardRecipients ?? Array.Empty<RewardRecipient>())
                    .Select(r => new { address = r.Address, percentage = r.Percentage })
            },
            ports
        };
    }

    private async Task<object> BuildPoolSnapInfoObjectAsync(PoolConfig poolCfg, int windowSec, CancellationToken ct)
    {
        var now = clock.Now;

        // PERSISTED: latest poolstats row (for network metrics & historical compatibility)
        var persisted = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));

        // LIVE: pool ring for live hashrate
        var ring = LiveHashrateState.ForPool(poolCfg.Id);
        var diffSum = ring.SumWindow(windowSec);
        var sharesPerSec = diffSum / Math.Max(1d, windowSec);

        var poolInst = TryGetPoolInstance(poolCfg.Id);
        var unit = ResolveUnit(poolCfg.Template.Family);
        var currentHashrate = DiffToHashrate(poolCfg, diffSum, windowSec, poolInst);

        // LIVE: round state (actualShares)
        var (startedAt, roundHeight, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);

        // PERSISTED: expectedShares via network difficulty from DB
        var expectedShares = persisted?.NetworkDifficulty ?? 0d;
        var luckPercent = expectedShares > 0 ? (actualShares / expectedShares) * 100.0 : 0.0;

        // LIVE: online miners via in-memory address window, not from persisted ConnectedMiners
        var (addressesOnline, _) = CountOnlineNow(poolCfg.Id, windowSec);

        return new
        {
            poolId = poolCfg.Id,

            coin = MapCoinMeta(poolCfg),
            pool = MapPoolStatic(poolCfg),

            // LIVE: in-memory window based metrics
            live = new
            {
                unit,
                windowSec,
                currentHashrate,
                sharesPerSec,
                minersOnline = addressesOnline
            },

            // PERSISTED: network data still from DB
            network = new
            {
                height = ToU64(persisted?.BlockHeight),
                difficulty = persisted?.NetworkDifficulty ?? 0d,
                hashrate = persisted?.NetworkHashrate ?? 0d
            },

            // MIXED: live actualShares + persisted expectedShares
            round = new
            {
                height = roundHeight.HasValue ? roundHeight.Value : ToU64(persisted?.BlockHeight),
                startedAt,
                actualShares,
                expectedShares,
                luckPercent
            }
        };
    }

    private async Task<object> BuildPoolSnapshotObjectAsync(PoolConfig poolCfg, int windowSec, CancellationToken ct)
    {
        var now = clock.Now;

        // PERSISTED: latest poolstats
        var persisted = await cf.Run(con =>
            statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));

        // LIVE: pool ring
        var ring = LiveHashrateState.ForPool(poolCfg.Id);
        var diffSum = ring.SumWindow(windowSec);
        var sharesPerSec = diffSum / Math.Max(1d, windowSec);

        var poolInst = TryGetPoolInstance(poolCfg.Id);
        var currentHashrate = DiffToHashrate(poolCfg, diffSum, windowSec, poolInst);
        var unit = ResolveUnit(poolCfg.Template.Family);

        // LIVE: round info
        var (startedAt, roundHeight, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);

        // PERSISTED: expected via difficulty
        var expectedShares = persisted?.NetworkDifficulty ?? 0d;
        var luckPercent = expectedShares > 0 ? (actualShares / expectedShares) * 100.0 : 0.0;

        // LIVE: online miners from address-based presence, not from ConnectedMiners
        var (addressesOnline, _) = CountOnlineNow(poolCfg.Id, windowSec);

        // LIVE: top 50 miners now aggregated by address
        var topMiners = LiveHashrateState.EnumeratePoolAddresses(poolCfg.Id, windowSec)
            .Select(m =>
            {
                var h = DiffToHashrate(poolCfg, m.diffSum, windowSec, poolInst);
                return new
                {
                    address = m.address,
                    hashrate = h,
                    online = IsOnlineFromLast(m.lastSeenMax, windowSec),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .Take(50)
            .ToArray();

        return new
        {
            poolId = poolCfg.Id,
            asOf = now,
            windowSec = windowSec,
            unit = unit,

            // LIVE: from in-memory rings
            currentHashrate = currentHashrate,
            sharesPerSec = sharesPerSec,
            minersOnline = addressesOnline,

            // MIXED: live actualShares + persisted difficulty
            round = new
            {
                height = roundHeight.HasValue ? roundHeight.Value : ToU64(persisted?.BlockHeight),
                startedAt,
                actualShares = actualShares,
                expectedShares = expectedShares,
                luckPercent = luckPercent
            },

            // PERSISTED: network
            network = new
            {
                height = ToU64(persisted?.BlockHeight),
                difficulty = persisted?.NetworkDifficulty ?? 0d,
                hashrate = persisted?.NetworkHashrate ?? 0d
            },

            topMinersNow = topMiners,

            // Backwards-compatible alias
            hashrate = currentHashrate
        };
    }

    private static int? TryGetConnectedMiners(IMiningPool poolInst)
    {
        if (poolInst == null) return null;

        // Try classic Stats.ConnectedMiners first
        var statsProp = poolInst.GetType().GetProperty("Stats");
        if (statsProp != null)
        {
            var stats = statsProp.GetValue(poolInst);
            if (stats != null)
            {
                var cmProp = stats.GetType().GetProperty("ConnectedMiners");
                if (cmProp != null && cmProp.PropertyType == typeof(int))
                    return (int)cmProp.GetValue(stats);
            }
        }

        // Fallback: attempt several common property names
        var direct = new[] { "ConnectedMiners", "MinerCount", "ConnectedClients" };
        foreach (var name in direct)
        {
            var p = poolInst.GetType().GetProperty(name);
            if (p != null && p.PropertyType == typeof(int))
                return (int)p.GetValue(poolInst);
        }

        return null;
    }

    private static (int addressesOnline, int workersOnline) CountOnlineNow(string poolId, int windowSec)
    {
        // LIVE: window-based presence, tolerant to reconnects/proxies
        var grace = Math.Max(windowSec, Live.LiveHashrateState.OnlineGraceSec);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var addrSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int workers = 0;

        // Iterate live worker table (address.miner granularity)
        foreach (var w in Live.LiveHashrateState.EnumeratePoolWorkers(poolId))
        {
            if (w.lastSeen <= 0) continue;
            var alive = (now - w.lastSeen) <= grace;
            if (!alive) continue;

            workers++;
            if (!string.IsNullOrEmpty(w.address))
                addrSet.Add(w.address);
        }

        return (addrSet.Count, workers);
    }

    private static int EffectiveWindow(int configuredWindowSec, int ageSec)
    {
        var conf = Math.Max(1, configuredWindowSec);

        if (ageSec <= 0)
            return conf;

        // age mínimo para não termos média em 2 ou 3 segundos
        var age = Math.Max(ageSec, MinEffectiveWindowSec);

        // nunca maior do que a janela configurada
        if (age > conf)
            age = conf;

        return age;
    }


    //**************************************************************
    // CONTROL ENDPOINTS
    //**************************************************************

    // ----------------------------------------------------------------
    // GET /api/live/version
    // ----------------------------------------------------------------


    [HttpGet("version")]
    public ActionResult<object> GetVersion()
    {
        var asm = Assembly.GetEntryAssembly();

        return Ok(new
        {
            product = "Miningcore",
            version = Program.GetVersion(),
            framework = RuntimeInformation.FrameworkDescription.Trim(),
            os = RuntimeInformation.OSDescription.Trim(),
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            assembly = asm?.GetName().Name,
            assemblyVersion = asm?.GetName().Version?.ToString()
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/health
    // ----------------------------------------------------------------

    [HttpGet("health")]
    public ActionResult<object> GetHealth()
    {
        var poolsCount = clusterConfig?.Pools?.Count(x => x.Enabled) ?? 0;

        return Ok(new
        {
            status = "ok",
            poolsEnabled = poolsCount,
            timestamp = DateTime.UtcNow
        });
    }


    //**************************************************************
    // HEAVY ENDPOINTS (LIVE + DB)
    // - Use live in-memory rings (LiveHashrateState, LiveRoundState)
    // - Also hit DB for persisted stats (poolstats, shares, payments)
    //**************************************************************

    // ----------------------------------------------------------------
    // GET /api/live/pools/snapshot
    // COST: LIVE (hashrate/round/online) + DB (network stats)
    // ----------------------------------------------------------------
    [HttpGet("pools/snapshot")]
    public async Task<ActionResult<object>> GetAllPoolsSnapshotAsync([FromQuery] int? windowSec)
    {
        var ct = HttpContext.RequestAborted;
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        var now = clock.Now;

        var enabled = clusterConfig.Pools?.Where(p => p.Enabled) ?? Enumerable.Empty<PoolConfig>();
        var items = new List<object>();

        foreach (var poolCfg in enabled)
            items.Add(await BuildPoolSnapshotObjectAsync(poolCfg, win, ct));

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { asOf = now, windowSec = win, pools = items });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/snapshot
    // COST: LIVE (hashrate/round/online) + DB (network stats)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/snapshot")]
    public async Task<PoolSnapshotResponse> GetPoolSnapshotAsync(string poolId,
        [FromQuery] int windowSec = DefaultWindowSec)
    {
        windowSec = Math.Clamp(windowSec, MinWindowSec, MaxWindowSec);

        var poolCfg = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // PERSISTED
        var persisted = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));
        var unit = ResolveUnit(poolCfg.Template.Family);

        // LIVE
        var ring = LiveHashrateState.ForPool(poolCfg.Id);
        var diffSum = ring.SumWindow(windowSec);
        var sharesPerSec = diffSum / Math.Max(1d, windowSec);

        var poolInst = TryGetPoolInstance(poolCfg.Id);
        var current = DiffToHashrate(poolCfg, diffSum, windowSec, poolInst);

        var (startedAt, roundHeight, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);
        var expected = persisted?.NetworkDifficulty ?? 0d;
        var luck = expected > 0 ? (actualShares / expected) * 100.0 : 0.0;

        // LIVE: online miners
        var (addressesOnline, _) = CountOnlineNow(poolCfg.Id, windowSec);

        var resp = new PoolSnapshotResponse
        {
            PoolId = poolCfg.Id,
            AsOf = clock.Now,
            WindowSec = windowSec,
            Unit = unit,
            CurrentHashrate = current,
            SharesPerSec = sharesPerSec,

            // LIVE: not using persisted ConnectedMiners anymore
            MinersOnline = addressesOnline,

            Network = new PoolNetworkInfo
            {
                Height = ToU64(persisted?.BlockHeight),
                Difficulty = persisted?.NetworkDifficulty ?? 0,
                Hashrate = persisted?.NetworkHashrate ?? 0
            },
            Round = new PoolRoundInfo
            {
                Height = roundHeight.HasValue ? roundHeight.Value : ToU64(persisted?.BlockHeight),
                StartedAt = startedAt,
                ActualShares = actualShares,
                ExpectedShares = expected,
                LuckPercent = luck
            }
        };

        Response.Headers["Cache-Control"] = "no-store";
        return resp;
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners/{address}/snapshot
    // COST: LIVE ONLY (no DB) - per-miner snapshot based on rings
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners/{address}/snapshot")]
    public ActionResult<MinerSnapshotResponse> GetMinerSnapshotAsync(
        string poolId, string address,
        [FromQuery] int windowSec = DefaultWindowSec)
    {
        windowSec = Math.Clamp(windowSec, MinWindowSec, MaxWindowSec);

        var poolCfg = GetPool(poolId);

        if (string.IsNullOrWhiteSpace(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.BadRequest);

        address = address.Trim();

        var unit = ResolveUnit(poolCfg.Template.Family);

        // LIVE: address window + age
        var (diffSum, lastMax, ageSec) =
            LiveHashrateState.GetAddressWindowWithAge(poolCfg.Id, address, windowSec);

        var effectiveWin = EffectiveWindow(windowSec, ageSec);

        var sharesPerSec = diffSum / Math.Max(1d, effectiveWin);

        var poolInst = TryGetPoolInstance(poolCfg.Id);
        var current = DiffToHashrate(poolCfg, diffSum, effectiveWin, poolInst);

        var online = IsOnlineFromLast(lastMax, windowSec);

        var resp = new MinerSnapshotResponse
        {
            PoolId = poolCfg.Id,
            Address = address,
            AsOf = clock.Now,
            WindowSec = windowSec,
            Unit = unit,
            Online = online,
            LastShareAt = lastMax > 0
                ? DateTimeOffset.FromUnixTimeSeconds(lastMax).UtcDateTime
                : null,
            CurrentHashrate = current,
            SharesPerSec = sharesPerSec,

            // NOTE: difficulty-assigned / reject / stale would require extra tracking
            DifficultyAssigned = 0,
            RejectPercentWindow = 0,
            StalePercentWindow = 0
        };

        Response.Headers["Cache-Control"] = "no-store";
        return resp;
    }


    // ----------------------------------------------------------------
    // GET /api/live/pools/snapinfo
    // COST: LIVE (hashrate/round/online) + DB (network stats)
    // ----------------------------------------------------------------
    [HttpGet("pools/snapinfo")]
    public async Task<ActionResult<object>> GetAllPoolsSnapInfoAsync([FromQuery] int? windowSec)
    {
        var ct = HttpContext.RequestAborted;
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        var now = clock.Now;

        var enabled = clusterConfig.Pools?.Where(p => p.Enabled) ?? Enumerable.Empty<PoolConfig>();
        var items = new List<object>();

        foreach (var poolCfg in enabled)
            items.Add(await BuildPoolSnapInfoObjectAsync(poolCfg, win, ct));

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { asOf = now, windowSec = win, pools = items });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/snapinfo
    // COST: LIVE (hashrate/round/online) + DB (network stats)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/snapinfo")]
    public async Task<ActionResult<object>> GetPoolSnapInfoAsync(
        string poolId,
        [FromQuery] int? windowSec)
    {
        var ct = HttpContext.RequestAborted;
        var now = clock.Now;

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var poolCfg = GetPool(poolId);

        if (!poolCfg.Enabled)
            return NotFound(new { error = "Pool disabled", poolId });

        var payload = await BuildPoolSnapInfoObjectAsync(poolCfg, win, ct);

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            windowSec = win,
            pool = payload
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/round
    // COST: LIVE (actualShares) + DB (expected via difficulty)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/round")]
    public async Task<ActionResult<object>> GetRoundNowAsync(string poolId)
    {
        var poolCfg = GetPool(poolId);
        var ct = HttpContext.RequestAborted;

        // PERSISTED: difficulty
        var persisted = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));

        // LIVE: round actualShares
        var (startedAt, height, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);

        var expectedShares = persisted?.NetworkDifficulty ?? 0d;
        var luckPercent = expectedShares > 0 ? (actualShares / expectedShares) * 100.0 : 0.0;

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new
        {
            poolId = poolCfg.Id,
            startedAt,
            height,
            actualShares,
            expectedShares,
            luckPercent
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/status
    // COST: LIVE (hashrate) + DB (network stats) per pool
    // ----------------------------------------------------------------
    [HttpGet("status")]
    public async Task<ActionResult<object>> GetClusterStatusAsync([FromQuery] int? windowSec)
    {
        var now = clock.Now;
        var pools = (clusterConfig.Pools ?? Array.Empty<PoolConfig>())
            .Where(p => p.Enabled)
            .ToArray();

        var ct = HttpContext.RequestAborted;
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var summaries = await Task.WhenAll(pools.Select(async poolCfg =>
        {
            // PERSISTED
            var persisted = await cf.Run(con => statsRepo.GetLastPoolStatsAsync(con, poolCfg.Id, ct));
            var unit = ResolveUnit(poolCfg.Template.Family);

            // LIVE
            var diffSum = LiveHashrateState.ForPool(poolCfg.Id).SumWindow(win);
            var poolInst = TryGetPoolInstance(poolCfg.Id);
            var current = DiffToHashrate(poolCfg, diffSum, win, poolInst);

            // LIVE: online miners via window-based presence
            var (addressesOnline, _) = CountOnlineNow(poolCfg.Id, win);

            return new
            {
                poolId = poolCfg.Id,
                coin = poolCfg.Template.Symbol,
                algo = poolCfg.Template.Family.ToString(),
                currentHashrate = current,
                minersOnline = addressesOnline,
                blockHeight = ToU64(persisted?.BlockHeight),
                difficulty = persisted?.NetworkDifficulty ?? 0d,
                unit,
                windowSec = win
            };
        }));

        var resultPools = summaries.Select(s => new
        {
            poolId = s.poolId,
            coin = s.coin,
            algo = s.algo,
            currentHashrate = s.currentHashrate,
            minersOnline = s.minersOnline,
            blockHeight = s.blockHeight,
            difficulty = s.difficulty,
            unit = s.unit,
            windowSec = win
        }).ToArray();

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            pools = resultPools,
            totalPools = resultPools.Length,
            totalMiners = resultPools.Sum(p => p.minersOnline),
            totalHashrate = resultPools.Sum(p => p.currentHashrate)
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners
    // COST: LIVE (hashrate/online) + DB (pendingShares via bulk SUM(shares.difficulty))
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners")]
    public async Task<IActionResult> GetPoolMiners(
        string poolId,
        [FromQuery] int windowSec = DefaultWindowSec,
        [FromQuery] int limit = DefaultLimit)
    {
        windowSec = Math.Clamp(windowSec, MinWindowSec, MaxWindowSec);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var poolCfg = GetPool(poolId);
        var ct = HttpContext.RequestAborted;
        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var (startedAt, _, _) = LiveRoundState.Snapshot(poolCfg.Id);
        var roundStart = startedAt;

        // LIVE: hashrate + online from in-memory ring
        var minersNow = LiveHashrateState
            .EnumeratePoolAddresses(poolCfg.Id, windowSec)
            .Select(m =>
            {
                var sharesPerSec = m.diffSum / Math.Max(1d, windowSec);
                var hashrate = DiffToHashrate(poolCfg, m.diffSum, windowSec, poolInst);

                return new
                {
                    address = m.address,
                    hashrate,
                    sharesPerSec,
                    online = IsOnlineFromLast(m.lastSeenMax, windowSec),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .Take(limit)
            .ToArray();

        // DB: bulk pendingShares per address
        var items = new List<object>(minersNow.Length);

        var addresses = minersNow
            .Select(x => x.address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IDictionary<string, MinerStats> statsByAddress;

        if (addresses.Length > 0)
        {
            statsByAddress = await cf.Run(con =>
                statsRepo.GetMinerStatsBulkAsync(con, poolCfg.Id, addresses, ct));
        }
        else
        {
            statsByAddress = new Dictionary<string, MinerStats>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var m in minersNow)
        {
            double pendingShares = 0;

            if (!string.IsNullOrWhiteSpace(m.address) &&
                statsByAddress.TryGetValue(m.address, out var stats) &&
                stats != null)
            {
                pendingShares = stats.PendingShares;

                // Keep parity with Bitcoin share multiplier if configured
                if (poolCfg.Template.Family == CoinFamily.Bitcoin)
                {
                    var bt = poolCfg.Template.As<BitcoinTemplate>();
                    if (bt != null && bt.ShareMultiplier > 0)
                        pendingShares *= bt.ShareMultiplier;
                }
            }

            items.Add(new
            {
                address = m.address,
                hashrate = m.hashrate,
                sharesPerSecond = m.sharesPerSec,
                pendingShares,
                online = m.online,
                lastShareAt = m.lastShareAt,
            });
        }

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            poolId = poolCfg.Id,
            unit,
            windowSec,
            round = new
            {
                startedAt = roundStart
            },
            items
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners-all
    // COST: LIVE (hashrate/online) + DB (pendingShares) + pagination
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners-all")]
    public async Task<IActionResult> GetPoolALLMiners(
        string poolId,
        [FromQuery] int? windowSec,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxLimit);

        var poolCfg = GetPool(poolId);
        var ct = HttpContext.RequestAborted;
        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var (startedAt, _, _) = LiveRoundState.Snapshot(poolCfg.Id);
        var roundStart = startedAt;

        // LIVE: all miners in window, sorted by hashrate
        var all = LiveHashrateState
            .EnumeratePoolAddresses(poolCfg.Id, win)
            .Select(m =>
            {
                var sharesPerSec = m.diffSum / Math.Max(1d, win);
                var hashrate = DiffToHashrate(poolCfg, m.diffSum, win, poolInst);

                return new
                {
                    address = m.address,
                    hashrate,
                    sharesPerSec,
                    online = IsOnlineFromLast(m.lastSeenMax, win),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .ToList();

        var totalItems = all.Count;
        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var skip = (page - 1) * pageSize;
        var pageItems = all
            .Skip(skip)
            .Take(pageSize)
            .ToArray();

        // DB: pendingShares only for page addresses
        var result = new List<object>(pageItems.Length);

        var pageAddresses = pageItems
            .Select(x => x.address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        IDictionary<string, MinerStats> statsByAddress;

        if (pageAddresses.Length > 0)
        {
            statsByAddress = await cf.Run(con =>
                statsRepo.GetMinerStatsBulkAsync(con, poolCfg.Id, pageAddresses, ct));
        }
        else
        {
            statsByAddress = new Dictionary<string, MinerStats>(StringComparer.OrdinalIgnoreCase);
        }

        foreach (var m in pageItems)
        {
            double pendingShares = 0;

            if (!string.IsNullOrWhiteSpace(m.address) &&
                statsByAddress.TryGetValue(m.address, out var stats) &&
                stats != null)
            {
                pendingShares = stats.PendingShares;

                if (poolCfg.Template.Family == CoinFamily.Bitcoin)
                {
                    var bt = poolCfg.Template.As<BitcoinTemplate>();
                    if (bt != null && bt.ShareMultiplier > 0)
                        pendingShares *= bt.ShareMultiplier;
                }
            }

            result.Add(new
            {
                address = m.address,
                hashrate = m.hashrate,
                sharesPerSecond = m.sharesPerSec,
                pendingShares,
                online = m.online,
                lastShareAt = m.lastShareAt
            });
        }

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            poolId = poolCfg.Id,
            unit,
            windowSec = win,
            round = new
            {
                startedAt = roundStart
            },
            page,
            pageSize,
            totalItems,
            totalPages,
            items = result
        });
    }

    //***************************** SSE *****************************\\
    // COST: LIVE ONLY (no DB, minimal payload)

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/feed (SSE)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/feed")]
    public async Task FeedAsync(
        string poolId,
        [FromQuery] int intervalSec = 2,
        [FromQuery] int? windowSec = null)
    {
        var poolCfg = GetPool(poolId);
        intervalSec = Math.Clamp(intervalSec, 1, 10);

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        Response.Headers["Cache-Control"] = "no-store";
        Response.ContentType = "text/event-stream";

        var ct = HttpContext.RequestAborted;
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        while (!ct.IsCancellationRequested)
        {
            var unit = ResolveUnit(poolCfg.Template.Family);
            var diffSum = LiveHashrateState.ForPool(poolCfg.Id).SumWindow(win);
            var current = DiffToHashrate(poolCfg, diffSum, win, poolInst);

            var nowIso = DateTime.UtcNow.ToString("o");
            var payload =
                $"data: {{\"poolId\":\"{poolCfg.Id}\",\"asOf\":\"{nowIso}\",\"unit\":\"{unit}\",\"windowSec\":{win},\"currentHashrate\":{current}}}\n\n";

            await Response.WriteAsync(payload, ct);
            await Response.Body.FlushAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(intervalSec), ct);
        }
    }

    //************************** LITE COST **************************\\
    // LITE ENDPOINTS
    // - LIVE ONLY (no DB queries)
    // - Designed for UI polling / dashboards / cards

    // ----------------------------------------------------------------
    // GET /api/live/miners/search-lite?q=&limit=100  (address-level)
    // COST: LIVE ONLY
    // ----------------------------------------------------------------
    [HttpGet("miners/search-lite")]
    public ActionResult<object> SearchMinersLite(
        [FromQuery] string q,
        [FromQuery] int limit = DefaultLimit,
        [FromQuery] int? windowSec = null)
    {
        q ??= string.Empty;

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var items = clusterConfig.Pools?.Where(p => p.Enabled).SelectMany(p =>
                LiveHashrateState.EnumeratePoolAddresses(p.Id, win)
                    .Where(m => m.address.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .Select(m => new
                    {
                        address = m.address,
                        poolId = p.Id,
                        lastSeen = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null,
                        online = IsOnlineFromLast(m.lastSeenMax, win),
                        sharesPerSec = m.diffSum / Math.Max(1d, win),
                        windowSec = win
                    }))
            ?? Enumerable.Empty<object>();

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { items = items.Take(limit) });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/top-miners-lite  (address-level)
    // COST: LIVE ONLY
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/top-miners-lite")]
    public ActionResult<object> GetTopMinersNowAsyncLite(
        string poolId,
        [FromQuery] int windowSec = DefaultWindowSec,
        [FromQuery] int limit = DefaultLimit)
    {
        windowSec = Math.Clamp(windowSec, MinWindowSec, MaxWindowSec);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var poolCfg = GetPool(poolId);
        var unit = ResolveUnit(poolCfg.Template.Family);

        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var miners = LiveHashrateState.EnumeratePoolAddresses(poolCfg.Id, windowSec)
            .Select(m =>
            {
                var h = DiffToHashrate(poolCfg, m.diffSum, windowSec, poolInst);

                return new
                {
                    address = m.address,
                    hashrate = h,
                    online = IsOnlineFromLast(m.lastSeenMax, windowSec),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .Take(limit)
            .ToArray();

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { poolId = poolCfg.Id, unit, windowSec, items = miners });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners-lite
    // COST: LIVE ONLY (no pendingShares, no DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners-lite")]
    public IActionResult GetPoolMinersLite(
    string poolId,
    [FromQuery] int windowSec = DefaultWindowSec,
    [FromQuery] int limit = DefaultLimit)
    {
        windowSec = Math.Clamp(windowSec, MinWindowSec, MaxWindowSec);
        limit = Math.Clamp(limit, 1, MaxLimit);

        var poolCfg = GetPool(poolId);
        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var (startedAt, _, _) = LiveRoundState.Snapshot(poolCfg.Id);
        var roundStart = startedAt;

        var minersNow = LiveHashrateState
            .EnumeratePoolAddresses(poolCfg.Id, windowSec)
            .Select(m =>
            {
                var sharesPerSec = m.diffSum / Math.Max(1d, windowSec);
                var hashrate = DiffToHashrate(poolCfg, m.diffSum, windowSec, poolInst);

                return new
                {
                    address = m.address,
                    hashrate,
                    sharesPerSec,
                    online = IsOnlineFromLast(m.lastSeenMax, windowSec),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .Take(limit)
            .ToArray();

        // NOTE: pendingShares intentionally not included (no DB in lite endpoints)
        var items = minersNow.Select(m => new
        {
            address = m.address,
            hashrate = m.hashrate,
            sharesPerSecond = m.sharesPerSec,
            online = m.online,
            lastShareAt = m.lastShareAt,
        });

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            poolId = poolCfg.Id,
            unit,
            windowSec,
            round = new
            {
                startedAt = roundStart
            },
            items
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/static-lite
    // COST: STATIC CONFIG ONLY (clusterConfig)
    // ----------------------------------------------------------------
    [HttpGet("pools/static-lite")]
    public ActionResult<object> GetPoolsStaticLite()
    {
        var items = (clusterConfig.Pools ?? Array.Empty<PoolConfig>())
            .Where(p => p.Enabled)
            .Select(p => new
            {
                poolId = p.Id,
                coin = MapCoinMeta(p),
                pool = MapPoolStatic(p),
                unit = ResolveUnit(p.Template.Family)
            })
            .ToArray();

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new { items });
    }

    // ----------------------------------------------------------------
    // GET /api/live/status-lite
    // COST: LIVE ONLY (no DB)
    // ----------------------------------------------------------------
    [HttpGet("status-lite")]
    public ActionResult<object> GetClusterStatusLite([FromQuery] int? windowSec)
    {
        var now = clock.Now;

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var enabled = clusterConfig.Pools?.Where(p => p.Enabled) ?? Enumerable.Empty<PoolConfig>();

        var pools = enabled.Select(poolCfg =>
        {
            var unit = ResolveUnit(poolCfg.Template.Family);

            var ring = LiveHashrateState.ForPool(poolCfg.Id);
            var diffSum = ring.SumWindow(win);
            var poolInst = TryGetPoolInstance(poolCfg.Id);
            var hashrate = DiffToHashrate(poolCfg, diffSum, win, poolInst);

            var (addressesOnline, _) = CountOnlineNow(poolCfg.Id, win);

            return new
            {
                poolId = poolCfg.Id,
                coin = poolCfg.Template.Symbol,
                algo = poolCfg.Template.Family.ToString(),
                unit,
                windowSec = win,
                currentHashrate = hashrate,
                minersOnline = addressesOnline
            };
        }).ToArray();

        Response.Headers["Cache-Control"] = "no-store";

        var totalMiners = pools.Sum(p => p.minersOnline);
        var totalHashrate = pools.Sum(p => p.currentHashrate);

        return Ok(new
        {
            asOf = now,
            pools,
            totalPools = pools.Length,
            totalMiners,
            totalHashrate
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/online-lite?mode=window|live&windowSec=...
    // COST: LIVE ONLY (either window-based presence or poolInst.Stats)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/online-lite")]
    public ActionResult<object> GetPoolOnlineWorkerLite(string poolId, [FromQuery] string mode, [FromQuery] int? windowSec)
    {
        var poolCfg = GetPool(poolId);
        if (!poolCfg.Enabled)
            return NotFound(new { error = "Pool disabled", poolId });

        var now = clock.Now;
        var useWindow = string.Equals(mode ?? "window", "window", StringComparison.OrdinalIgnoreCase);

        int win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        int minersOnline;
        int workersOnline = 0;

        var poolInst = TryGetPoolInstance(poolCfg.Id);

        if (useWindow)
        {
            var t = CountOnlineNow(poolCfg.Id, win);
            minersOnline = t.addressesOnline;
            workersOnline = t.workersOnline;
        }
        else
        {
            // NOTE: instant snapshot from poolInst, not recommended for UI but cheap
            minersOnline = TryGetConnectedMiners(poolInst) ?? 0;
        }

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new
        {
            asOf = now,
            poolId = poolCfg.Id,
            mode = useWindow ? "window" : "live",
            windowSec = useWindow ? win : (int?)null,
            minersOnline,
            workersOnline = useWindow ? workersOnline : (int?)null
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/online-lite?mode=window|live&windowSec=...
    // COST: LIVE ONLY (no DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/online-lite")]
    public ActionResult<object> GetPoolsOnlineWorkersLite([FromQuery] string mode, [FromQuery] int? windowSec)
    {
        var now = clock.Now;
        var useWindow = string.Equals(mode ?? "window", "window", StringComparison.OrdinalIgnoreCase);
        int win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var enabled = clusterConfig.Pools?.Where(p => p.Enabled) ?? Enumerable.Empty<PoolConfig>();

        var items = enabled.Select(p =>
        {
            int miners;
            int workers = 0;

            if (useWindow)
            {
                var t = CountOnlineNow(p.Id, win);
                miners = t.addressesOnline;
                workers = t.workersOnline;
            }
            else
            {
                var inst = TryGetPoolInstance(p.Id);
                miners = TryGetConnectedMiners(inst) ?? 0;
            }

            return new
            {
                poolId = p.Id,
                unit = ResolveUnit(p.Template.Family),
                mode = useWindow ? "window" : "live",
                windowSec = useWindow ? win : (int?)null,
                minersOnline = miners,
                workersOnline = useWindow ? workers : (int?)null
            };
        }).ToArray();

        Response.Headers["Cache-Control"] = "no-store";
        return Ok(new
        {
            asOf = now,
            items,
            totalPools = items.Length,
            totalMiners = items.Sum(x => x.minersOnline)
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/snapshot-lite
    // COST: LIVE ONLY (all pools, no DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/snapshot-lite")]
    public ActionResult<object> GetAllPoolsSnapshotLite([FromQuery] int? windowSec)
    {
        var now = clock.Now;

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var enabled = clusterConfig.Pools?.Where(p => p.Enabled) ?? Enumerable.Empty<PoolConfig>();

        var items = new List<object>();
        double totalHashrate = 0;
        int totalMiners = 0;

        foreach (var poolCfg in enabled)
        {
            var unit = ResolveUnit(poolCfg.Template.Family);
            var poolInst = TryGetPoolInstance(poolCfg.Id);

            var ring = LiveHashrateState.ForPool(poolCfg.Id);
            var diffSum = ring.SumWindow(win);
            var sharesPerSec = diffSum / Math.Max(1d, win);
            var currentHashrate = DiffToHashrate(poolCfg, diffSum, win, poolInst);

            var t = CountOnlineNow(poolCfg.Id, win);
            var minersOnline = t.addressesOnline;

            var (startedAt, roundHeight, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);

            items.Add(new
            {
                poolId = poolCfg.Id,
                unit,
                windowSec = win,

                currentHashrate,
                sharesPerSec,
                minersOnline,

                round = new
                {
                    height = roundHeight,
                    startedAt,
                    actualShares
                }
            });

            totalHashrate += currentHashrate;
            totalMiners += minersOnline;
        }

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            windowSec = win,
            pools = items,
            totalPools = items.Count,
            totalMiners,
            totalHashrate
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners/{address}/round-lite
    // COST: LIVE ONLY (per-miner + global round)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners/{address}/round-lite")]
    public ActionResult<object> GetMinerRoundLite(
        string poolId, string address,
        [FromQuery] int? windowSec)
    {
        var poolCfg = GetPool(poolId);
        if (string.IsNullOrWhiteSpace(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.BadRequest);

        address = address.Trim();

        var now = clock.Now;
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var (diffSum, lastMax, ageSec) =
            LiveHashrateState.GetAddressWindowWithAge(poolCfg.Id, address, win);

        var effectiveWin = EffectiveWindow(win, ageSec);

        var sharesPerSec = diffSum / Math.Max(1d, effectiveWin);
        var currentHashrate = DiffToHashrate(poolCfg, diffSum, effectiveWin, poolInst);
        var online = IsOnlineFromLast(lastMax, win);

        var (roundStartedAt, roundHeight, roundActualShares) =
            LiveRoundState.Snapshot(poolCfg.Id);

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            poolId = poolCfg.Id,
            address,
            unit = ResolveUnit(poolCfg.Template.Family),
            windowSec = win,

            online,
            lastShareAt = lastMax > 0
                ? DateTimeOffset.FromUnixTimeSeconds(lastMax).UtcDateTime
                : (DateTime?)null,
            currentHashrate,
            sharesPerSec,

            round = new
            {
                height = roundHeight,
                startedAt = roundStartedAt,
                actualShares = roundActualShares
            }
        });
    }


    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/snapshot-lite
    // COST: LIVE ONLY (per pool snapshot without DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/snapshot-lite")]
    public ActionResult<object> GetPoolSnapshotLite(
        string poolId,
        [FromQuery] int? windowSec)
    {
        var poolCfg = GetPool(poolId);

        if (!poolCfg.Enabled)
            return NotFound(new { error = "Pool disabled", poolId });

        var now = clock.Now;

        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var ring = LiveHashrateState.ForPool(poolCfg.Id);
        var diffSum = ring.SumWindow(win);
        var sharesPerSec = diffSum / Math.Max(1d, win);
        var currentHashrate = DiffToHashrate(poolCfg, diffSum, win, poolInst);

        var t = CountOnlineNow(poolCfg.Id, win);
        var minersOnline = t.addressesOnline;

        var (startedAt, roundHeight, actualShares) = LiveRoundState.Snapshot(poolCfg.Id);

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            poolId = poolCfg.Id,
            unit,
            windowSec = win,

            currentHashrate,
            sharesPerSec,
            minersOnline,

            round = new
            {
                height = roundHeight,
                startedAt,
                actualShares
            }
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners-all-lite
    // COST: LIVE ONLY (all miners, paginated, no DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners-all-lite")]
    public IActionResult GetPoolALLMinersLite(
        string poolId,
        [FromQuery] int? windowSec,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize)
    {
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxLimit);

        var poolCfg = GetPool(poolId);
        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        var (startedAt, _, _) = LiveRoundState.Snapshot(poolCfg.Id);
        var roundStart = startedAt;

        var all = LiveHashrateState
            .EnumeratePoolAddresses(poolCfg.Id, win)
            .Select(m =>
            {
                var sharesPerSec = m.diffSum / Math.Max(1d, win);
                var hashrate = DiffToHashrate(poolCfg, m.diffSum, win, poolInst);

                return new
                {
                    address = m.address,
                    hashrate,
                    sharesPerSec,
                    online = IsOnlineFromLast(m.lastSeenMax, win),
                    lastShareAt = m.lastSeenMax > 0
                            ? DateTimeOffset.FromUnixTimeSeconds(m.lastSeenMax).UtcDateTime
                            : (DateTime?)null
                };
            })
            .OrderByDescending(x => x.hashrate)
            .ToList();

        var totalItems = all.Count;
        var totalPages = (int)Math.Ceiling(totalItems / (double)pageSize);

        var skip = (page - 1) * pageSize;
        var pageItems = all
            .Skip(skip)
            .Take(pageSize)
            .ToArray();

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            poolId = poolCfg.Id,
            unit,
            windowSec = win,
            round = new
            {
                startedAt = roundStart
            },
            page,
            pageSize,
            totalItems,
            totalPages,
            items = pageItems
        });
    }

    // ----------------------------------------------------------------
    // GET /api/live/pools/{poolId}/miners/{address}/workers-lite
    // COST: LIVE ONLY (per-worker under one address, no DB)
    // ----------------------------------------------------------------
    [HttpGet("pools/{poolId}/miners/{address}/workers-lite")]
    public ActionResult<object> GetMinerWorkersLite(
        string poolId,
        string address,
        [FromQuery] int? windowSec)
    {
        var poolCfg = GetPool(poolId);

        if (string.IsNullOrWhiteSpace(address))
            throw new ApiException("Invalid or missing miner address", HttpStatusCode.BadRequest);

        address = address.Trim();

        var now = clock.Now;
        var win = Math.Clamp(windowSec ?? DefaultWindowSec, MinWindowSec, MaxWindowSec);

        var unit = ResolveUnit(poolCfg.Template.Family);
        var poolInst = TryGetPoolInstance(poolCfg.Id);

        // LIVE: workers do address com diffSum + lastSeen + ageSec
        var workers = LiveHashrateState
            .EnumerateAddressWorkersWithAge(poolCfg.Id, address, win)
            .Select(w =>
            {
                var effectiveWin = EffectiveWindow(win, w.ageSec);

                var sharesPerSec = w.diffSum / Math.Max(1d, effectiveWin);
                var hashrate = DiffToHashrate(poolCfg, w.diffSum, effectiveWin, poolInst);
                var online = IsOnlineFromLast(w.lastSeenMax, win);
                var lastShareAt = w.lastSeenMax > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(w.lastSeenMax).UtcDateTime
                    : (DateTime?)null;

                return new
                {
                    worker = w.worker,           // string após o ponto: address.worker
                    hashrate,
                    sharesPerSecond = sharesPerSec,
                    online,
                    lastShareAt,
                    effectiveWindowSec = effectiveWin
                };
            })
            .OrderByDescending(x => x.hashrate)
            .ToArray();

        Response.Headers["Cache-Control"] = "no-store";

        return Ok(new
        {
            asOf = now,
            poolId = poolCfg.Id,
            address,
            unit,
            windowSec = win,   // janela "configurada"
            items = workers
        });
    }

}

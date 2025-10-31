using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using Autofac;
using AutoMapper;
using Microsoft.AspNetCore.Mvc;
using Miningcore.Api.Responses;
using Miningcore.Configuration;
using Miningcore.Persistence;
using Miningcore.Util;

namespace Miningcore.Api.Controllers;

public abstract class ApiControllerBase : ControllerBase
{
    protected ApiControllerBase(IComponentContext ctx)
    {
        mapper = ctx.Resolve<IMapper>();
        clusterConfig = ctx.Resolve<ClusterConfig>();
        cf = ctx.Resolve<IConnectionFactory>();
    }

    protected readonly ClusterConfig clusterConfig;
    protected readonly IConnectionFactory cf;
    protected readonly IMapper mapper;

    protected void EnsureAdminAccess()
    {
        var remoteAddress = HttpContext?.Connection?.RemoteIpAddress;

        if(remoteAddress == null)
            throw new ApiException("Forbidden", HttpStatusCode.Forbidden);

        if(remoteAddress.IsIPv4MappedToIPv6)
            remoteAddress = remoteAddress.MapToIPv4();

        var whitelist = clusterConfig.Api?.AdminIpWhitelist != null ?
            new List<IPAddress>(clusterConfig.Api.AdminIpWhitelist.Select(IPAddress.Parse)) :
            new List<IPAddress>();

        if(whitelist.Count == 0)
        {
            whitelist.Add(IPAddress.Loopback);
            whitelist.Add(IPAddress.IPv6Loopback);
            whitelist.Add(IPUtils.IPv4LoopBackOnIPv6);
        }

        var normalizedWhitelist = new HashSet<IPAddress>();

        foreach(var address in whitelist)
        {
            normalizedWhitelist.Add(address);

            if(address.IsIPv4MappedToIPv6)
                normalizedWhitelist.Add(address.MapToIPv4());
        }

        if(!normalizedWhitelist.Contains(remoteAddress))
            throw new ApiException("Forbidden", HttpStatusCode.Forbidden);
    }

    protected PoolConfig GetPoolNoThrow(string poolId)
    {
        if(string.IsNullOrEmpty(poolId))
            return null;

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);
        return pool;
    }

    protected PoolConfig GetPool(string poolId)
    {
        if(string.IsNullOrEmpty(poolId))
            throw new ApiException("Invalid pool id", HttpStatusCode.NotFound);

        var pool = clusterConfig.Pools.FirstOrDefault(x => x.Id == poolId && x.Enabled);

        if(pool == null)
            throw new ApiException($"Unknown pool {poolId}", HttpStatusCode.NotFound);

        return pool;
    }

    protected static UptimeInfo CreateUptimeInfo(TimeSpan span)
    {
        if(span < TimeSpan.Zero)
            span = TimeSpan.Zero;

        span = TimeSpan.FromSeconds(Math.Floor(span.TotalSeconds));

        return new UptimeInfo
        {
            Days = span.Days,
            Hours = span.Hours,
            Minutes = span.Minutes
        };
    }
}

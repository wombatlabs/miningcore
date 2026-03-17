// Kaspa/KaspaClientFactory.cs
using System.Net.Http;
using Grpc.Net.Client;
using System.Net;
using Miningcore.Blockchain.Kaspa.Configuration;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Mining;
using NLog;
using kaspad = Miningcore.Blockchain.Kaspa.Kaspad;

namespace Miningcore.Blockchain.Kaspa;

public static class KaspaClientFactory
{
    public static kaspad.KaspadRPC.KaspadRPCClient CreateKaspadRPCClient(
        DaemonEndpointConfig[] daemonEndpoints,
        string protobufDaemonRpcServiceName)
    {
        var daemonEndpoint = daemonEndpoints.First();

        var baseUrl = new UriBuilder(
            daemonEndpoint.Ssl || daemonEndpoint.Http2 ? Uri.UriSchemeHttps : Uri.UriSchemeHttp,
            daemonEndpoint.Host, daemonEndpoint.Port, daemonEndpoint.HttpPath);

        var channel = GrpcChannel.ForAddress(baseUrl.ToString(), new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                KeepAlivePingDelay = TimeSpan.FromSeconds(60),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
                EnableMultipleHttp2Connections = true
            },
            DisposeHttpClient = true,
            MaxReceiveMessageSize = 2 * 1024 * 1024,
            MaxSendMessageSize = 2 * 1024 * 1024
        });

        return new kaspad.KaspadRPC.KaspadRPCClient(
            new kaspad.KaspadRPC(protobufDaemonRpcServiceName), channel);
    }
}

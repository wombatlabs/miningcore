using Miningcore.Configuration;

namespace Miningcore.Blockchain.Kaspa.Configuration;

public class KaspaPoolConfigExtra
{
    /// <summary>
    /// Maximum number of tracked jobs.
    /// Default: 8
    /// </summary>
    public int? MaxActiveJobs { get; set; }
    
    /// <summary>
    /// Arbitrary string added in the Kaspa coinbase tx
    /// Default: "Miningcore.developers["MiningCore"]"
    /// </summary>
    public string ExtraData { get; set; }
    
    public int? ExtraNonce1Size { get; set; }
    
    /// <summary>
    /// Optional: Daemon RPC service name override
    /// Should match the value of .proto file
    /// Default: "protowire.RPC"
    /// </summary>
    public string ProtobufDaemonRpcServiceName { get; set; }
    
        /// <summary>
    /// Optional: Wallet RPC service name override
    /// Should match the value of .proto file
    /// Default: "kaspawalletd.kaspawalletd"
    /// </summary>
    public string ProtobufWalletRpcServiceName { get; set; }
}

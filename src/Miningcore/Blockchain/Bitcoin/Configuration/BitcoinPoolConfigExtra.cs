using Miningcore.Configuration;
using Miningcore.Mining;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Configuration;

public class BitcoinPoolConfigExtra
{
    public const int MaxExtraNonce2Size = 32;

    public BitcoinAddressType AddressType { get; set; } = BitcoinAddressType.Legacy;

    public string BechPrefix { get; set; } = "bc";

    /// <summary>
    /// CashAddr prefix (e.g. bitcoincash, bchtest, bitcoincashii)
    /// </summary>
    public string CashAddrPrefix { get; set; } = "bitcoincash";

    /// <summary>
    /// Size of extraNonce2 advertised via mining.subscribe, in bytes.
    /// Default: 4. Braiins Hashpower requires at least 7.
    /// </summary>
    public int? ExtraNonce2Size { get; set; }

    /// <summary>
    /// Maximum number of tracked jobs.
    /// Default: 12 - you should increase this value if your blockrefreshinterval is higher than 300ms
    /// </summary>
    public int? MaxActiveJobs { get; set; }

    /// <summary>
    /// Set to true to limit RPC commands to old Bitcoin command set
    /// </summary>
    public bool? HasLegacyDaemon { get; set; }

    /// <summary>
    /// Set to true to fall back to multiple sendtoaddress RPC calls for payments
    /// </summary>
    public bool HasBrokenSendMany { get; set; } = false;

    /// <summary>
    /// Arbitrary string appended at end of coinbase tx
    /// Overrides property of same name from BitcoinTemplate
    /// </summary>
    public string CoinbaseTxComment { get; set; }

    /// <summary>
    /// Blocktemplate stream published via ZMQ
    /// </summary>
    public ZmqPubSubEndpointConfig BtStream { get; set; }

    /// <summary>
    /// Custom Arguments for getblocktemplate RPC
    /// </summary>
    public JToken GBTArgs { get; set; }

    public static int GetExtraNonce2Size(BitcoinPoolConfigExtra extraPoolConfig, string poolId = null)
    {
        var result = extraPoolConfig?.ExtraNonce2Size ?? BitcoinConstants.Extranonce2Length;

        if(result <= 0 || result > MaxExtraNonce2Size)
            throw new PoolStartupException($"extraNonce2Size must be between 1 and {MaxExtraNonce2Size} bytes", poolId);

        return result;
    }
}

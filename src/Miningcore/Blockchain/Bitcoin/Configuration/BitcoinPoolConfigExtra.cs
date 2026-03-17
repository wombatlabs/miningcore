using Miningcore.Configuration;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Bitcoin.Configuration
{
    public class BitcoinPoolConfigExtra
    {
        public BitcoinAddressType AddressType { get; set; } = BitcoinAddressType.Legacy;

        // Allows overriding the Bech32 HRP (human-readable part) for SegWit addresses.
        // Example: "bc" (Bitcoin mainnet), "tb" (Bitcoin testnet), "bcrt" (regtest),
        //          "ltc" (Litecoin), "doge" (Dogecoin if applicable), etc.
        public string BechPrefix { get; set; } = "bc";

        /// <summary>
        /// Maximum number of tracked jobs.
        /// Default: 12 - increase if your blockrefreshinterval is higher than 300ms.
        /// </summary>
        public int? MaxActiveJobs { get; set; }

        /// <summary>
        /// Set to true to limit RPC commands to old Bitcoin command set.
        /// </summary>
        public bool? HasLegacyDaemon { get; set; }

        /// <summary>
        /// Set to true to fall back to multiple sendtoaddress RPC calls for payments.
        /// </summary>
        public bool HasBrokenSendMany { get; set; } = false;

        /// <summary>
        /// Arbitrary string appended at end of coinbase tx.
        /// Overrides property of same name from BitcoinTemplate.
        /// </summary>
        public string CoinbaseTxComment { get; set; }

        /// <summary>
        /// Blocktemplate stream published via ZMQ.
        /// </summary>
        public ZmqPubSubEndpointConfig BtStream { get; set; }

        /// <summary>
        /// Custom arguments for getblocktemplate RPC.
        /// </summary>
        public JToken GBTArgs { get; set; }
    }
}

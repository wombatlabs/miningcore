using System;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Bitcoin
{
    public static class BitcoinUtils
    {
        /// <summary>
        /// Converts any valid address for the given network into an IDestination.
        /// Supports Base58 (P2PKH/P2SH) and Bech32/Bech32m (P2WPKH, P2WSH, Taproot v1, etc).
        /// IMPORTANT: Older NBitcoin versions expose ScriptPubKey.GetDestination() with NO parameters.
        /// </summary>
        public static IDestination AddressToDestination(string address, Network expectedNetwork)
        {
            if(string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("address is null/empty", nameof(address));
            if(expectedNetwork == null)
                throw new ArgumentNullException(nameof(expectedNetwork));

            var addr = BitcoinAddress.Create(address, expectedNetwork);

            // Use the parameterless overload to match your NBitcoin version
            var dest = addr.ScriptPubKey.GetDestination();
            if(dest == null)
                throw new FormatException($"Unable to derive destination from address '{address}' for network '{expectedNetwork.Name}'.");

            return dest;
        }

        /// <summary>
        /// Converts a Bech32 (or Bech32m) address to IDestination.
        /// If <paramref name="bechPrefix"/> is provided (for altchains with custom HRP),
        /// attempts to decode with that HRP when the default parser fails.
        /// </summary>
        public static IDestination BechSegwitAddressToDestination(string address, Network expectedNetwork, string bechPrefix = null)
        {
            if(string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("address is null/empty", nameof(address));
            if(expectedNetwork == null)
                throw new ArgumentNullException(nameof(expectedNetwork));

            try
            {
                return AddressToDestination(address, expectedNetwork);
            }
            catch
            {
                if(string.IsNullOrWhiteSpace(bechPrefix))
                    throw;

                // Manual HRP decode fallback (rarely needed)
                var encoder = Encoders.Bech32(bechPrefix);
                var prog = encoder.Decode(address, out var witVersion);

                if(witVersion == 0 && prog?.Length == 20)
                    return new WitKeyId(prog);
                if(witVersion == 0 && prog?.Length == 32)
                    return new WitScriptId(prog);

                throw new FormatException($"Unsupported bech32 witness (v={witVersion}, len={prog?.Length}) for '{address}' (hrp='{bechPrefix}').");
            }
        }

        /// <summary>
        /// Converts a Bitcoin Cash address to IDestination using the altcoin's Network.
        /// </summary>
        public static IDestination BCashAddressToDestination(string address, Network expectedNetwork)
        {
            if(string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("address is null/empty", nameof(address));
            if(expectedNetwork == null)
                throw new ArgumentNullException(nameof(expectedNetwork));

            var bcash = NBitcoin.Altcoins.BCash.Instance.GetNetwork(expectedNetwork.ChainName)
                       ?? throw new ArgumentException($"Unable to resolve BCash network for chain '{expectedNetwork.ChainName}'.");

            var anyAddr = BitcoinAddress.Create(address, bcash);

            // Parameterless overload to satisfy your NBitcoin version
            var dest = anyAddr.ScriptPubKey.GetDestination()
                       ?? throw new FormatException($"Unable to derive destination from BCash address '{address}'.");

            return dest;
        }

        /// <summary>
        /// Converts a Litecoin address to IDestination using the altcoin's Network (base58 or bech32 ltc...).
        /// </summary>
        public static IDestination LitecoinAddressToDestination(string address, Network expectedNetwork)
        {
            if(string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("address is null/empty", nameof(address));
            if(expectedNetwork == null)
                throw new ArgumentNullException(nameof(expectedNetwork));

            var ltc = NBitcoin.Altcoins.Litecoin.Instance.GetNetwork(expectedNetwork.ChainName)
                    ?? throw new ArgumentException($"Unable to resolve Litecoin network for chain '{expectedNetwork.ChainName}'.");

            var addr = BitcoinAddress.Create(address, ltc);

            // Parameterless overload to satisfy your NBitcoin version
            var dest = addr.ScriptPubKey.GetDestination()
                       ?? throw new FormatException($"Unable to derive destination from Litecoin address '{address}'.");

            return dest;
        }
    }
}

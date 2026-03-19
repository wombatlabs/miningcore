using System.Diagnostics;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace Miningcore.Blockchain.Bitcoin;

public static class BitcoinUtils
{
    private const string CashAddrCharset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly int[] CashAddrCharsetMap = BuildCashAddrCharsetMap();

    /// <summary>
    /// Bitcoin addresses are implemented using the Base58Check encoding of the hash of either:
    /// Pay-to-script-hash(p2sh): payload is: RIPEMD160(SHA256(redeemScript)) where redeemScript is a
    /// script the wallet knows how to spend; version byte = 0x05 (these addresses begin with the digit '3')
    /// Pay-to-pubkey-hash(p2pkh): payload is RIPEMD160(SHA256(ECDSA_publicKey)) where
    /// ECDSA_publicKey is a public key the wallet knows the private key for; version byte = 0x00
    /// (these addresses begin with the digit '1')
    /// The resulting hash in both of these cases is always exactly 20 bytes.
    /// </summary>
    public static IDestination AddressToDestination(string address, Network expectedNetwork)
    {
        var decoded = Encoders.Base58Check.DecodeData(address);
        var networkVersionBytes = expectedNetwork.GetVersionBytes(Base58Type.PUBKEY_ADDRESS, true);
        decoded = decoded.Skip(networkVersionBytes.Length).ToArray();
        var result = new KeyId(decoded);

        return result;
    }

    public static IDestination BechSegwitAddressToDestination(string address, Network expectedNetwork, string bechPrefix)
    {
        var encoder = Encoders.Bech32(bechPrefix);
        var decoded = encoder.Decode(address, out var witVersion);
        var result = new WitKeyId(decoded);

        Debug.Assert(result.GetAddress(expectedNetwork).ToString() == address);
        return result;
    }

    public static IDestination BCashAddressToDestination(string address, Network expectedNetwork)
    {
        var bcash = NBitcoin.Altcoins.BCash.Instance.GetNetwork(expectedNetwork.ChainName);
        var trashAddress = bcash.Parse<NBitcoin.Altcoins.BCash.BTrashPubKeyAddress>(address);
        return trashAddress.ScriptPubKey.GetDestinationAddress(bcash);
    }

    public static IDestination CashAddrToDestination(string address, string expectedPrefix)
    {
        if(string.IsNullOrEmpty(address))
            throw new ArgumentException("Address must not be empty", nameof(address));

        expectedPrefix = string.IsNullOrEmpty(expectedPrefix) ? "bitcoincash" : expectedPrefix.ToLowerInvariant();

        // CashAddr is case-insensitive but mixed case is not allowed
        var hasLower = address.Any(char.IsLower);
        var hasUpper = address.Any(char.IsUpper);
        if(hasLower && hasUpper)
            throw new FormatException("Invalid CashAddr: mixed case");

        address = address.ToLowerInvariant();

        string prefix;
        string payload;
        var separatorIndex = address.IndexOf(':');

        if(separatorIndex >= 0)
        {
            prefix = address.Substring(0, separatorIndex);
            payload = address.Substring(separatorIndex + 1);

            if(!string.Equals(prefix, expectedPrefix, StringComparison.Ordinal))
                throw new FormatException($"Invalid CashAddr prefix '{prefix}' (expected '{expectedPrefix}')");
        }
        else
        {
            prefix = expectedPrefix;
            payload = address;
        }

        var data = DecodeCashAddrPayload(prefix, payload);
        if(data.Length < 1)
            throw new FormatException("Invalid CashAddr payload");

        // convert 5-bit groups to 8-bit bytes (with padding)
        var payloadBytes = ConvertBits(data, 5, 8, true);
        if(payloadBytes == null || payloadBytes.Length < 2)
            throw new FormatException("Invalid CashAddr payload");

        var version = payloadBytes[0];
        var addressType = (byte) (version >> 3);
        var size = version & 0x07;
        var hashSize = size switch
        {
            0 => 20,
            1 => 24,
            2 => 28,
            3 => 32,
            4 => 40,
            5 => 48,
            6 => 56,
            7 => 64,
            _ => throw new FormatException("Invalid CashAddr hash size")
        };

        if(payloadBytes.Length < 1 + hashSize)
            throw new FormatException("Invalid CashAddr payload length");

        var hash = payloadBytes.Skip(1).Take(hashSize).ToArray();
        var extra = payloadBytes.Skip(1 + hashSize).ToArray();

        // padding bytes must be zero
        if(extra.Any(b => b != 0))
            throw new FormatException("Invalid CashAddr padding");

        return addressType switch
        {
            0 => new KeyId(hash),    // P2PKH
            1 => new ScriptId(hash), // P2SH
            _ => throw new FormatException("Unsupported CashAddr address type")
        };
    }

    public static IDestination LitecoinAddressToDestination(string address, Network expectedNetwork)
    {
        var litecoin = NBitcoin.Altcoins.Litecoin.Instance.GetNetwork(expectedNetwork.ChainName);
        var encoder = litecoin.GetBech32Encoder(Bech32Type.WITNESS_PUBKEY_ADDRESS, true);

        var decoded = encoder.Decode(address, out var witVersion);
        var result = new WitKeyId(decoded);

        Debug.Assert(result.GetAddress(litecoin).ToString() == address);
        return result;
    }

    private static int[] BuildCashAddrCharsetMap()
    {
        var map = new int[128];
        Array.Fill(map, -1);

        for(var i = 0; i < CashAddrCharset.Length; i++)
            map[CashAddrCharset[i]] = i;

        return map;
    }

    private static byte[] DecodeCashAddrPayload(string prefix, string payload)
    {
        if(string.IsNullOrEmpty(payload))
            throw new FormatException("Invalid CashAddr payload");

        var data = new int[payload.Length];
        for(var i = 0; i < payload.Length; i++)
        {
            var c = payload[i];
            if(c >= 128)
                throw new FormatException("Invalid CashAddr character");

            var val = CashAddrCharsetMap[c];
            if(val < 0)
                throw new FormatException("Invalid CashAddr character");

            data[i] = val;
        }

        if(!VerifyCashAddrChecksum(prefix, data))
            throw new FormatException("Invalid CashAddr checksum");

        // remove checksum (last 8 chars)
        return data.Take(data.Length - 8).Select(x => (byte) x).ToArray();
    }

    private static bool VerifyCashAddrChecksum(string prefix, IReadOnlyList<int> payload)
    {
        var values = PrefixExpand(prefix).Concat(payload).ToArray();
        return CashAddrPolymod(values) == 0;
    }

    private static int CashAddrPolymod(IReadOnlyList<int> values)
    {
        long c = 1;
        foreach(var d in values)
        {
            var c0 = (int)(c >> 35);
            c = ((c & 0x07ffffffffL) << 5) ^ d;

            if((c0 & 0x01) != 0) c ^= 0x98f2bc8e61;
            if((c0 & 0x02) != 0) c ^= 0x79b76d99e2;
            if((c0 & 0x04) != 0) c ^= 0xf33e5fb3c4;
            if((c0 & 0x08) != 0) c ^= 0xae2eabe2a8;
            if((c0 & 0x10) != 0) c ^= 0x1e4f43e470;
        }

        return (int)(c ^ 1);
    }

    private static IEnumerable<int> PrefixExpand(string prefix)
    {
        foreach(var ch in prefix)
            yield return ch & 0x1f;

        yield return 0;
    }

    private static byte[] ConvertBits(IReadOnlyList<byte> data, int fromBits, int toBits, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var ret = new List<byte>();
        var maxv = (1 << toBits) - 1;

        foreach(var value in data)
        {
            if(value < 0 || (value >> fromBits) != 0)
                return null;

            acc = (acc << fromBits) | value;
            bits += fromBits;

            while(bits >= toBits)
            {
                bits -= toBits;
                ret.Add((byte)((acc >> bits) & maxv));
            }
        }

        if(pad)
        {
            if(bits > 0)
                ret.Add((byte)((acc << (toBits - bits)) & maxv));
        }
        else
        {
            if(bits >= fromBits || ((acc << (toBits - bits)) & maxv) != 0)
                return null;
        }

        return ret.ToArray();
    }
}

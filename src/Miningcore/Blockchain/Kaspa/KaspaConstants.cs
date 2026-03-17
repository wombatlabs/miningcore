// Kaspa/KaspaConstants.cs
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Miningcore.Blockchain.Kaspa
{
    // ReSharper disable InconsistentNaming
    public static class KaspaConstants
    {
        public const string WalletDaemonCategory = "wallet";
        public const int    Diff1TargetNumZero   = 31;
        public static readonly double  Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
        public static readonly decimal SmallestUnit             = 100_000_000m; // Sompi per KAS

        public const string ProtobufDaemonRpcServiceName = "protowire.RPC";

        // Bitcoin-like Diff1 target (0x00ffff0000..)
        public static readonly BigInteger Diff1Target =
            BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);

        public static readonly BigInteger MinHash = BigInteger.One;

        public const byte PubKeyAddrID       = 0x00;
        public const byte PubKeyECDSAAddrID  = 0x01;
        public const byte ScriptHashAddrID   = 0x08;

        public const int  PublicKeySize        = 32; // schnorr-like
        public const int  PublicKeySizeECDSA   = 33; // secp256k1 compressed
        public const int  Blake2bSize256       = 32;

        public const int  NonceLength = 16;                 // 8 bytes = 16 hex
        public const int  ExtranoncePlaceHolderLength = 8;  // bytes

        public static readonly Regex RegexUserAgentBzMiner       = new(@"bzminer", RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static readonly Regex RegexUserAgentGodMiner      = new(@"godminer|gminer", RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static readonly Regex RegexUserAgentIceRiverMiner = new(@"iceriver", RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static readonly Regex RegexUserAgentGoldShell     = new(@"goldshell", RegexOptions.IgnoreCase|RegexOptions.Compiled);
        public static readonly Regex RegexUserAgentTNNMiner      = new(@"tnn", RegexOptions.IgnoreCase|RegexOptions.Compiled);

        public const string CoinbaseBlockHash       = "kaspa:cbh";
        public const string CoinbaseProofOfWorkHash = "kaspa:powh";
        public const string CoinbaseHeavyHash       = "kaspa:heavyhash";

        public static readonly Dictionary<byte, string> KaspaAddressType = new()
        {
            { 0x00, "Public Key Address" },
            { 0x01, "Public Key ECDSA Address" },
            { 0x08, "Script Hash Address" },
        };
    }

    public static class KarlsencoinConstants
    {
        public const ulong FishHashForkHeightTestnet      = 0;
        public const ulong FishHashPlusForkHeightTestnet  = 43200;
        public const ulong FishHashPlusForkHeightMainnet  = 26962009;

        public const int CoinbaseSize = 80;
    }

    public static class PyrinConstants
    {
        public const ulong Blake3ForkHeight = 1484741;
    }

    public static class SpectreConstants
    {
        public const int Diff1TargetNumZero = 7;
        public static readonly BigInteger Diff1b =
            BigInteger.Parse("00ffff0000000000000000000000000000000000000000000000000000000000", NumberStyles.HexNumber);
        public static readonly double     Pow2xDiff1TargetNumZero = Math.Pow(2, Diff1TargetNumZero);
        public static readonly BigInteger MinHash = BigInteger.One;

        public const int CoinbaseSize = 80;
    }

    public enum KaspaBech32Prefix
    {
        Unknown = 0,
        KaspaMain,
        KaspaDev,
        KaspaTest,
        KaspaSim
    }
}

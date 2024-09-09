using System.Collections.Generic;

namespace Miningcore.Blockchain
{
    public static class DevDonation
    {
        public const decimal Percent = 0.01m;

        public static readonly Dictionary<string, string> Addresses = new Dictionary<string, string>
        {
            { "BTC", "1HdZy5SPhrNxPF8A7XHPkbXJ4T3s22h8Yi" },
            { "BCH", "qr6k8uvwvq4rh9uk2edn8cf67m2wp33cvylu5eu28d" },
            { "BSV", "1PFWcE3K9f9bvyPRiFjxaJkTzxARv7W1Vq" },
            { "DOGE", "DBxgxkkVvmWSyoCUPqb2c9MZ7JsD2JKCNu" },
            { "DGB", "DNYz7hZAe4uDr88L1tKfaHqBjLFGUusguT" },
            { "LTC", "LNMfes31Yk6pPgruV3mZcAvxwQ2DcoAVcW" }
        };
    }

    public static class CoinMetaData
    {
        public const string BlockHeightPH = "$height$";
        public const string BlockHashPH = "$hash$";
    }
}
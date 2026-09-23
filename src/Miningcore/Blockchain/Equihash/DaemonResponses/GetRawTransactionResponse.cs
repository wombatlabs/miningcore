namespace Miningcore.Blockchain.Equihash.DaemonResponses;

/// <summary>
/// Subset of the getrawtransaction (verbose = 1) response used for block classification.
/// Zebra exposes hex, height and confirmations by default; the decoded vin/vout are only present
/// when the insightexplorer option is enabled, so we do not rely on them.
/// </summary>
public class ZCashRawTransaction
{
    public string Hex { get; set; }
    public long Height { get; set; }
    public long Confirmations { get; set; }
}

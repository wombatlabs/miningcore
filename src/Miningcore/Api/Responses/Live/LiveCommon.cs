// Api/Responses/Live/LiveCommon.cs
using System;
using Miningcore.Configuration;

namespace Miningcore.Api.Responses.Live;

/// <summary>
/// Small helper utilities shared by live API responses.
/// Keep this class dependency-free and conservative to avoid build issues across forks.
/// </summary>
public static class LiveCommon
{
    /// <summary>
    /// Difficulty-1 constant (2^32). Used to approximate H/s from shares-per-second.
    /// </summary>
    public const double Diff1 = 4294967296.0;

    /// <summary>
    /// Convert shares-per-second at a given share difficulty into hashrate (H/s) approximation.
    /// </summary>
    public static double HpsFromShares(double sharesPerSec, double shareDifficulty) =>
        sharesPerSec * shareDifficulty * Diff1;

    /// <summary>
    /// Return a human-readable unit for live hashrate.
    /// Equihash-based families conventionally use "Sol/s"; all others default to "H/s".
    /// We intentionally keep the switch conservative to avoid enum drift across forks.
    /// </summary>
    public static string ResolveUnit(CoinFamily family) => family switch
    {
        CoinFamily.Equihash => "Sol/s",
        _ => "H/s"
    };

    /// <summary>
    /// Normalize a miner address for comparisons and display.
    /// We avoid referencing specific coin families that may not exist in some forks.
    /// As a pragmatic heuristic: if the address looks like an EVM hex address (starts with "0x"),
    /// normalize to lowercase; otherwise leave as-is.
    /// </summary>
    public static string NormalizeMinerAddress(CoinFamily family, string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return address;

        if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return address.ToLowerInvariant();

        return address;
    }
}

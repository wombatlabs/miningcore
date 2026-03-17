using Miningcore.Blockchain.Progpow;

namespace Miningcore.Blockchain.Progpow.Kawpow.Custom.Frencoin
{
    /// <summary>
    /// FREN behaves like plain KawPoW (RVN-like):
    /// - No founders / masternodes / special coinbase payloads
    /// - Same Diff1 semantics as RVN (uint256 max)
    /// - Uses default ProgpowJob hot-path (header/coinbase, mix-hash, varDiff, block target)
    /// 
    /// We intentionally do not override anything to keep the fast path untouched.
    /// If in the future FREN adds a special rule, override the required virtuals here.
    /// </summary>
    public class FrencoinJob : ProgpowJob
    {
        // No overrides needed for standard KawPoW behavior.
        // If FREN ever needs special coinbase/tag rules, override:
        // - SerializeCoinbase(...)
        // - SerializeHeader(...)
        // - ProcessShareInternal(...)
        // and keep allocations minimal (Span/ArrayPool like base).
    }
}

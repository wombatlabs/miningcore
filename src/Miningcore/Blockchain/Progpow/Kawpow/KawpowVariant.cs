using System.Numerics;
using Miningcore.Configuration;

namespace Miningcore.Blockchain.Progpow.Kawpow

{
    /// <summary>
    /// Selects profile parameters at Init() time. The hot path then uses cached fields.
    /// </summary>
    public static class KawpowVariant
    {
        public static (BigInteger diff1, int extranonceLen) SelectFor(CoinTemplate coin)
        {
            // Default to KawPoW profile (Ravencoin-family)
            var diff1 = KawpowConstants.Diff1; // aliases RavencoinConstants.Diff1
            var extra = KawpowConstants.ExtranoncePlaceHolderLength;

            // Hook for future variants (firopow etc.) if needed.

            return (diff1, extra);
        }
    }
}

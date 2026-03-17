using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Miningcore.Blockchain.Progpow
{
    public static class ProgpowUtils
    {
        // We keep a fixed-point scale to avoid string/decimal conversions and reduce FP error.
        // 10^12 is enough precision for stratum difficulty math.
        private const ulong SCALE = 1_000_000_000_000UL;
        private static readonly BigInteger BI_SCALE = new BigInteger((long) SCALE);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string EncodeTargetFixed(BigInteger diff1b, double difficulty)
        {
            // --- Protocol correctness guards ---
            // Non-finite or non-positive difficulties are clamped to 1.0 (standard pool behavior).
            if(!double.IsFinite(difficulty) || difficulty <= 0d)
                difficulty = 1d;

            // Convert difficulty to an integer fixed-point (scaled) using decimal to minimize rounding error.
            // This avoids "1.0.ToString()" and sub-strings entirely.
            decimal d = (decimal) difficulty;

            // Avoid over/underflow on extreme values
            if(d > (decimal) ulong.MaxValue / SCALE)
                d = (decimal) ulong.MaxValue / SCALE;
            else if(d < 1m / SCALE)
                d = 1m / SCALE;

            ulong diffScaled = (ulong) Math.Round(d * SCALE, MidpointRounding.AwayFromZero);
            if(diffScaled == 0) diffScaled = 1;

            // target = floor( (Diff1B * SCALE) / round(difficulty * SCALE) )
            // This equals floor(Diff1B / difficulty) with fixed-point rounding.
            BigInteger numerator = diff1b * BI_SCALE;
            BigInteger t = BigInteger.Divide(numerator, new BigInteger((long) diffScaled));

            // Clamp to sane protocol bounds: [1, Diff1B]
            if(t.Sign <= 0) t = BigInteger.One;
            else if(t > diff1b) t = diff1b;

            // Return 256-bit hex (64 lowercase chars) as miners expect.
            return string.Format("{0:x64}", t);
        }

        // Public helpers (drop-in replacements)
        public static string FiroEncodeTarget(double difficulty) =>
            EncodeTargetFixed(FiroConstants.Diff1B, difficulty);

        public static string RavencoinEncodeTarget(double difficulty) =>
            EncodeTargetFixed(RavencoinConstants.Diff1B, difficulty);
    }
}

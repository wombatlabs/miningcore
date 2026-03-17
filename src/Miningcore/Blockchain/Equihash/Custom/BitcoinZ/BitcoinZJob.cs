// src/Miningcore/Blockchain/Equihash/Custom/BitcoinZ/BitcoinZJob.cs
using System.Collections.Generic;
using System.IO;
using System.Text;
using Miningcore.Blockchain.Equihash.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Extensions;
using Miningcore.Time;
using System.Linq;
using NBitcoin;
using NLog;

// Tags are used like this:
// "extra": { "coinbaseString": "TAG IN HUMAN WORDS" }
// "extra": { "coinbaseTag": "hex:2F4861736853746F726D2F" }



namespace Miningcore.Blockchain.Equihash.Custom.BitcoinZ
{
    /// <summary>
    /// BitcoinZ-specific job:
    /// - Reads pool tag (CoinbaseString) from config
    /// - Injects the tag into the coinbase ScriptSig
    /// - Rebuilds coinbase and merkle so everything matches the tagged ScriptSig
    /// </summary>
    public class BitcoinZJob : Equihash.EquihashJob
    {
        // Logger
        private static readonly NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        public override void Init(EquihashBlockTemplate blockTemplate, string jobId,
    PoolConfig poolConfig, ClusterConfig clusterConfig, IMasterClock clock,
    IDestination poolAddressDestination, Network network,
    EquihashSolver solver)
        {
            // Initialize base (builds coinbase without tag)
            base.Init(blockTemplate, jobId, poolConfig, clusterConfig, clock, poolAddressDestination, network, solver);

            // Read coinbase tag from pool-level Extra first, fallback to cluster PaymentProcessing.CoinbaseString
            string tagText = null;

            try
            {
                if(poolConfig?.Extra is Newtonsoft.Json.Linq.JObject rawExtra)
                {
                    // Accept multiple keys for convenience
                    tagText = (string) (rawExtra["coinbaseString"] ?? rawExtra["coinbaseTag"] ?? rawExtra["tag"]);
                }
            }
            catch { /* ignore malformed Extra */ }

            if(string.IsNullOrWhiteSpace(tagText))
                tagText = clusterConfig?.PaymentProcessing?.CoinbaseString;

            // Convert to bytes (ASCII by default; HEX if "hex:..." prefix is present)
            poolTagBytes = ParseCoinbaseTag(tagText);

            if(poolTagBytes != null && poolTagBytes.Length > 0)
                logger?.Debug(() => $"[BTCZ] Applied coinbase tag {tagText} with ({poolTagBytes.Length} bytes)");

            // If tag present, rebuild coinbase and merkle accordingly
            if(poolTagBytes != null && poolTagBytes.Length > 0)
            {
                BuildCoinbase(); // rebuild coinbase with tagged ScriptSig

                // Recompute merkle root and job params to match new coinbase
                var txHashes = new List<uint256> { new(coinbaseInitialHash) };
                txHashes.AddRange(BlockTemplate.Transactions.Select(tx => new uint256(tx.Hash.HexToReverseByteArray())));

                merkleRoot = MerkleNode.GetRoot(txHashes).Hash.ToBytes().ReverseInPlace();
                merkleRootReversed = merkleRoot.ReverseInPlace();
                merkleRootReversedHex = merkleRootReversed.ToHexString();

                var hashReserved = isSaplingActive && !string.IsNullOrEmpty(blockTemplate.FinalSaplingRootHash)
                    ? blockTemplate.FinalSaplingRootHash.HexToReverseByteArray().ToHexString()
                    : sha256Empty.ToHexString();

                jobParams = new object[]
                {
            JobId,
            BlockTemplate.Version.ReverseByteOrder().ToStringHex8(),
            previousBlockHashReversedHex,
            merkleRootReversedHex,
            hashReserved,
            BlockTemplate.CurTime.ReverseByteOrder().ToStringHex8(),
            BlockTemplate.Bits.HexToReverseByteArray().ToHexString(),
            false
                };
            }
        }

        // Local helper
        private static byte[] ParseCoinbaseTag(string text)
        {
            if(string.IsNullOrWhiteSpace(text))
                return null;

            // HEX mode when prefixed with "hex:"
            if(text.StartsWith("hex:", StringComparison.OrdinalIgnoreCase))
            {
                var hex = text.Substring(4).Trim();
                if(hex.Length == 0) return null;

                try
                {
                    var data = hex.HexToByteArray();
                    // Safety cap to keep ScriptSig small
                    if(data.Length > 60)
                        data = data[..60];
                    return data.Length > 0 ? data : null;
                }
                catch
                {
                    // Invalid hex -> ignore and fall back to no tag
                    return null;
                }
            }

            // Default: ASCII (strip control chars, limit size)
            var ascii = Encoding.ASCII.GetBytes(new string(text.Where(c => !char.IsControl(c)).ToArray()));
            if(ascii.Length == 0)
                return null;

            if(ascii.Length > 60)
                ascii = ascii[..60];

            return ascii;
        }



        protected override void BuildCoinbase()
        {
            // Base ScriptSig with BIP34 (height)
            var script = TxIn.CreateCoinbase((int) BlockTemplate.Height).ScriptSig;

            // Inject pool tag if present
            if(poolTagBytes != null && poolTagBytes.Length > 0)
            {
                var ops = new List<Op>(script.ToOps()) { Op.GetPushOp(poolTagBytes) };
                script = new Script(ops.ToArray());
            }

            // Outputs are identical to the base implementation
            txOut = CreateOutputTransaction();

            using(var stream = new MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                // Version / Overwinter
                if(isOverwinterActive)
                {
                    uint mask = 1u << 31;
                    uint versionWithOverwinter = txVersion | mask;
                    bs.ReadWrite(ref versionWithOverwinter);
                }
                else
                {
                    bs.ReadWrite(ref txVersion);
                }

                if(isOverwinterActive || isSaplingActive)
                    bs.ReadWrite(ref txVersionGroupId);

                // Simulated coinbase input (with tagged ScriptSig)
                bs.ReadWriteAsVarInt(ref txInputCount);
                bs.ReadWrite(sha256Empty);
                bs.ReadWrite(ref coinbaseIndex);
                bs.ReadWrite(ref script);
                bs.ReadWrite(ref coinbaseSequence);

                // Outputs
                var txOutBytes = SerializeOutputTransaction(txOut);
                bs.ReadWrite(txOutBytes);

                // Misc
                bs.ReadWrite(ref txLockTime);

                if(isOverwinterActive || isSaplingActive)
                {
                    txExpiryHeight = (uint) BlockTemplate.Height;
                    bs.ReadWrite(ref txExpiryHeight);
                }

                if(isSaplingActive)
                {
                    bs.ReadWrite(ref txBalance);
                    bs.ReadWriteAsVarInt(ref txVShieldedSpend);
                    bs.ReadWriteAsVarInt(ref txVShieldedOutput);
                }

                if(isOverwinterActive || isSaplingActive)
                    bs.ReadWriteAsVarInt(ref txJoinSplits);

                // Finalize buffers used by merkle construction
                coinbaseInitial = stream.ToArray();
                coinbaseInitialHash = new byte[32];
                sha256D.Digest(coinbaseInitial, coinbaseInitialHash);
            }
        }

    }
}

// src/Miningcore/Blockchain/Equihash/Custom/Ycash_Job.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Miningcore.Blockchain.Equihash;
using Miningcore.Blockchain.Equihash.DaemonResponses;

using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Crypto.Hashing.Equihash;
using Miningcore.Stratum;
using Miningcore.Time;
using NBitcoin;
/*
namespace Miningcore.Blockchain.Equihash.Custom.Ycash
{
    /// <summary>
    /// Ycash-specific Equihash job.
    /// - Keeps the exact base behavior for Overwinter/Sapling and reward routing.
    /// - Adds an optional pool tag (coinbase "extra" data) to ScriptSig.
    /// - Does NOT change wire-format, header, varints, or solution handling.
    /// </summary>
    public class YcashJob : EquihashJob
    {
        /// <summary>
        /// Optional coinbase tag (e.g., from cluster config PaymentProcessing.CoinbaseString).
        /// If null/empty, no tag is appended.
        /// </summary>
        private byte[] ycashPoolTagBytes;

        public override void Init(EquihashBlockTemplate blockTemplate, string jobId,
            PoolConfig poolConfig, ClusterConfig clusterConfig, IMasterClock clock,
            IDestination poolAddressDestination, Network network, EquihashSolver solver)
        {
            // Base initialization builds the tx/rewards and sets Overwinter/Sapling flags, etc.
            base.Init(blockTemplate, jobId, poolConfig, clusterConfig, clock, poolAddressDestination, network, solver);

            // Pick a tag string for the coinbase (purely cosmetic, safe for consensus).
            // Priority: cluster coinbase string -> pool template comment -> default "Miningcore".
            var tag = clusterConfig?.PaymentProcessing?.CoinbaseString;
            if (string.IsNullOrWhiteSpace(tag))
                tag = poolConfig?.Template?.CoinbaseTxComment ?? "Miningcore";

            // Encode once (ASCII is conventional for coinbase tags).
            ycashPoolTagBytes = string.IsNullOrWhiteSpace(tag) ? null : Encoding.ASCII.GetBytes(tag);
        }

        /// <summary>
        /// Build the coinbase transaction bytes with an extra push (pool tag) in the ScriptSig.
        /// Mirrors base implementation, only difference is appending the tag to ScriptSig before serialization.
        /// </summary>
        protected override void BuildCoinbase()
        {
            // 1) Start from the standard BIP34 ScriptSig (height)
            var baseScriptSig = TxIn.CreateCoinbase((int) BlockTemplate.Height).ScriptSig;

            // 2) Append pool tag if configured (safe, data-only push)
            Script scriptSig;
            if (ycashPoolTagBytes is { Length: > 0 })
            {
                // Merge ops: existing ScriptSig ops + push(tag)
                var ops = new List<Op>(baseScriptSig.ToOps()) { Op.GetPushOp(ycashPoolTagBytes) };
                scriptSig = new Script(ops);
            }
            else
                scriptSig = baseScriptSig;

            // 3) Create outputs as in the base class (handles all Ycash templates/flags)
            txOut = CreateOutputTransaction();

            // 4) Serialize the coinbase transaction initial part (mirrors EquihashJob base)
            using (var stream = new System.IO.MemoryStream())
            {
                var bs = new BitcoinStream(stream, true);

                // Version (with Overwinter bit if needed)
                if (isOverwinterActive)
                {
                    uint mask = 1u; // Overwinter on
                    uint shiftedMask = mask << 31;
                    uint versionWithOverwinter = txVersion | shiftedMask;
                    bs.ReadWrite(ref versionWithOverwinter);
                }
                else
                {
                    bs.ReadWrite(ref txVersion);
                }

                // Version GroupId for Overwinter/Sapling
                if (isOverwinterActive || isSaplingActive)
                {
                    bs.ReadWrite(ref txVersionGroupId);
                }

                // Inputs (coinbase)
                bs.ReadWriteAsVarInt(ref txInputCount);
                bs.ReadWrite(sha256Empty);          // prevout hash
                bs.ReadWrite(ref coinbaseIndex);    // prevout index (0xFFFFFFFF)
                bs.ReadWrite(ref scriptSig);        // <-- use tagged ScriptSig here
                bs.ReadWrite(ref coinbaseSequence); // sequence (0xFFFFFFFF)

                // Outputs
                var txOutBytes = SerializeOutputTransaction(txOut);
                bs.ReadWrite(txOutBytes);

                // Locktime
                bs.ReadWrite(ref txLockTime);

                // Zcash-family extensions (expiry height, spends/outputs/joinSplits)
                if (isOverwinterActive || isSaplingActive)
                {
                    txExpiryHeight = (uint) BlockTemplate.Height;
                    bs.ReadWrite(ref txExpiryHeight);
                }

                if (isSaplingActive)
                {
                    bs.ReadWrite(ref txBalance);
                    bs.ReadWriteAsVarInt(ref txVShieldedSpend);
                    bs.ReadWriteAsVarInt(ref txVShieldedOutput);
                }

                if (isOverwinterActive || isSaplingActive)
                {
                    bs.ReadWriteAsVarInt(ref txJoinSplits);
                }

                // Finalize
                coinbaseInitial = stream.ToArray();

                // Pre-hash coinbase initial for Merkle calculation (double SHA-256)
                coinbaseInitialHash = new byte[32];
                sha256D.Digest(coinbaseInitial, coinbaseInitialHash);
            }
        }
    }
}
*/
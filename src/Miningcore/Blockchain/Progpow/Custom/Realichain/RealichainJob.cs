using System.Globalization;
using System.Text;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Newtonsoft.Json.Linq;
using NLog;
using Transaction = NBitcoin.Transaction;

namespace Miningcore.Blockchain.Progpow.Custom.Realichain;

public class RealichainJob : ProgpowJob
{
    public override (Share Share, string BlockHex) ProcessShareInternal(ILogger logger,
        StratumConnection worker, ulong nonce, string inputHeaderHash, string mixHash)
    {
        var context = worker.ContextAs<ProgpowWorkerContext>();
        var extraNonce1 = context.ExtraNonce1;

        // build coinbase
        var coinbase = SerializeCoinbase(extraNonce1);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // hash block-header
        var headerBytes = SerializeHeader(coinbaseHash);
        Span<byte> headerHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, headerHash);
        headerHash.Reverse();

        var headerHashHex = headerHash.ToHexString();

        if(headerHashHex != inputHeaderHash)
            throw new StratumException(StratumError.MinusOne, $"bad header-hash");

        if(!progpowHasher.Compute(logger, (int) BlockTemplate.Height, headerHash.ToArray(), nonce, out var mixHashOut, out var resultBytes))
            throw new StratumException(StratumError.MinusOne, "bad hash");

        if(mixHash != mixHashOut.ToHexString())
            throw new StratumException(StratumError.MinusOne, $"bad mix-hash");

        resultBytes.ReverseInPlace();
        mixHashOut.ReverseInPlace();

        var resultValue = new uint256(resultBytes);
        var resultValueBig = resultBytes.AsSpan().ToBigInteger();
        // calc share-diff
        var shareDiff = (double) new BigRational(FiroConstants.Diff1, resultValueBig) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = resultValue <= blockTargetValue;

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier,
        };

        if(!isBlockCandidate)
        {
            return (result, null);
        }

        result.IsBlockCandidate = true;

        var nonceBytes = (Span<byte>) nonce.ToString("X").HexToReverseByteArray();
        var mixHashBytes = (Span<byte>) mixHash.HexToReverseByteArray();
        // concat headerBytes, nonceBytes and mixHashBytes
        Span<byte> headerBytesNonceMixHasBytes = stackalloc byte[headerBytes.Length + nonceBytes.Length + mixHashBytes.Length];
        headerBytes.CopyTo(headerBytesNonceMixHasBytes);
        var offset = headerBytes.Length;
        nonceBytes.CopyTo(headerBytesNonceMixHasBytes[offset..]);
        offset += nonceBytes.Length;
        mixHashBytes.CopyTo(headerBytesNonceMixHasBytes[offset..]);

        Span<byte> blockHash = stackalloc byte[32];
        blockHasher.Digest(headerBytesNonceMixHasBytes, blockHash);
        result.BlockHash = blockHash.ToHexString();

        var blockBytes = SerializeBlock(headerBytes, coinbase, nonce, mixHashOut);
        var blockHex = blockBytes.ToHexString();

        return (result, blockHex);
    }

    #region Masternodes

    protected override Money CreateMasternodeOutputs(Transaction tx, Money reward)
    {
        foreach(var mn in Enumerate<Masternode>((object) masterNodeParameters?.Masternode))
        {
            if(!string.IsNullOrEmpty(mn?.Script))
            {
                var spk = new Script(mn.Script.HexToByteArray());
                var pay = mn.Amount;
                tx.Outputs.Add(pay, spk);
                // REALI: não desconta
            }
        }

        return reward;
    }

    #endregion // Masternodes

    #region Community

    protected override Money CreateCommunityOutputs(Transaction tx, Money reward)
    {
        foreach(var c in Enumerate<Community>((object) communityParameters?.Community))
        {
            if(!string.IsNullOrEmpty(c?.Script))
            {
                var spk = new Script(c.Script.HexToByteArray());
                var pay = c.Amount;
                tx.Outputs.Add(pay, spk);
                // REALI: não desconta
            }
        }

        return reward;
    }

    #endregion //Community

    #region Developer

    protected override Money CreateDeveloperOutputs(Transaction tx, Money reward)
    {
        foreach(var d in Enumerate<Developer>((object) developerParameters?.Developer))
        {
            if(!string.IsNullOrEmpty(d?.Script))
            {
                var spk = new Script(d.Script.HexToByteArray());
                var pay = d.Amount;
                tx.Outputs.Add(pay, spk);
                // REALI: não desconta
            }
        }

        return reward;
    }

    #endregion //Developer

    private static IEnumerable<T> Enumerate<T>(object src)
    {
        if(src == null)
            yield break;

        if(src is JToken jt)
        {
            if(jt.Type == JTokenType.Array)
            {
                var arr = jt.ToObject<T[]>();
                if(arr != null)
                    foreach(var i in arr) yield return i;
            }
            else
            {
                var single = jt.ToObject<T>();
                if(single != null)
                    yield return single;
            }
            yield break;
        }

        if(src is IEnumerable<T> en)
        {
            foreach(var i in en) yield return i;
            yield break;
        }

        if(src is T t)
            yield return t;
    }
}

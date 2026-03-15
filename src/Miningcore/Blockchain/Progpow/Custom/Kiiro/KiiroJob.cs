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

namespace Miningcore.Blockchain.Progpow.Custom.Kiiro;

public class KiiroJob : ProgpowJob
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
            ShareDifficulty = shareDiff,
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
        if(masterNodeParameters.Masternode != null)
        {
            Masternode[] masternodes;

            // Dash v13 Multi-Master-Nodes
            if(masterNodeParameters.Masternode.Type == JTokenType.Array)
                masternodes = masterNodeParameters.Masternode.ToObject<Masternode[]>();
            else
                masternodes = new[] { masterNodeParameters.Masternode.ToObject<Masternode>() };

            if(masternodes != null)
            {
                foreach(var masterNode in masternodes)
                {
                    if(!string.IsNullOrEmpty(masterNode.Script))
                    {
                        Script payeeAddress = new (masterNode.Script.HexToByteArray());
                        var payeeReward = masterNode.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                    /*  A block reward of 30 KIIRO/block is divided as follows:
                    
                            Miners (20%, 6 KIIRO)
                            Masternodes (61%, 18.3 KIIRO)
                            DataMining Fund (1%, 0.3 KIIRO)
                            Developer Fund (9%, 2.7 KIIRO)
                            Community Fund (9%, 2.7 KIIRO)
                    */
                        //reward -= payeeReward; // KIIRO does not deduct payeeReward from coinbasevalue (reward) since it's the amount which goes to miners
                    }
                }
            }
        }

        return reward;
    }

    #endregion // Masternodes
 
    #region Community

    protected override Money CreateCommunityOutputs(Transaction tx, Money reward)
    {
        if (communityParameters.Community != null)
        {
            Community[] communitys;
            if (communityParameters.Community.Type == JTokenType.Array)
                communitys = communityParameters.Community.ToObject<Community[]>();
            else
                communitys = new[] { communityParameters.Community.ToObject<Community>() };

            if(communitys != null)
            {
                foreach(var Community in communitys)
                {
                    if(!string.IsNullOrEmpty(Community.Script))
                    {
                        Script payeeAddress = new (Community.Script.HexToByteArray());
                        var payeeReward = Community.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                    /*  A block reward of 30 KIIRO/block is divided as follows:
                    
                            Miners (20%, 6 KIIRO)
                            Masternodes (61%, 18.3 KIIRO)
                            DataMining Fund (1%, 0.3 KIIRO)
                            Developer Fund (9%, 2.7 KIIRO)
                            Community Fund (9%, 2.7 KIIRO)
                    */
                        //reward -= payeeReward; // KIIRO does not deduct payeeReward from coinbasevalue (reward) since it's the amount which goes to miners

                    }
                }
            }
        }

        return reward;
    }

    #endregion //Community
 
    #region Developer

    protected override Money CreateDeveloperOutputs(Transaction tx, Money reward)
    {
        if (developerParameters.Developer != null)
        {
            Developer[] developers;
            if (developerParameters.Developer.Type == JTokenType.Array)
                developers = developerParameters.Developer.ToObject<Developer[]>();
            else
                developers = new[] { developerParameters.Developer.ToObject<Developer>() };

            if(developers != null)
            {
                foreach(var Developer in developers)
                {
                    if(!string.IsNullOrEmpty(Developer.Script))
                    {
                        Script payeeAddress = new (Developer.Script.HexToByteArray());
                        var payeeReward = Developer.Amount;

                        tx.Outputs.Add(payeeReward, payeeAddress);
                    /*  A block reward of 30 KIIRO/block is divided as follows:
                    
                            Miners (20%, 6 KIIRO)
                            Masternodes (61%, 18.3 KIIRO)
                            DataMining Fund (1%, 0.3 KIIRO)
                            Developer Fund (9%, 2.7 KIIRO)
                            Community Fund (9%, 2.7 KIIRO)
                    */
                        //reward -= payeeReward; // KIIRO does not deduct payeeReward from coinbasevalue (reward) since it's the amount which goes to miners

                    }
                }
            }
        }

        return reward;
    }

    #endregion //Developer
}

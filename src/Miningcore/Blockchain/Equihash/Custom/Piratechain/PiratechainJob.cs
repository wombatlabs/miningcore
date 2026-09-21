using NBitcoin;

namespace Miningcore.Blockchain.Equihash.Custom.Piratechain;

public class PiratechainJob : EquihashJob
{
    protected override byte[] SerializeBlock(Span<byte> header, Span<byte> coinbase, Span<byte> solution)
    {
        var transactionCount = (uint) BlockTemplate.Transactions.Length + 1; // +1 for prepended coinbase tx
        var rawTransactionBuffer = BuildRawTransactionBuffer();

        using(var stream = new MemoryStream())
        {
            var bs = new BitcoinStream(stream, true);

            bs.ReadWrite(header);
            bs.ReadWrite(solution);

            // transaction count as Bitcoin CompactSize (VarInt).
            // Note: the previous hex-string encoding here corrupted the count for blocks with 10 or
            // more transactions (e.g. 10 -> 0x10), producing invalid blocks.
            bs.ReadWriteAsVarInt(ref transactionCount);

            bs.ReadWrite(coinbase);
            bs.ReadWrite(rawTransactionBuffer);

            return stream.ToArray();
        }
    }
}
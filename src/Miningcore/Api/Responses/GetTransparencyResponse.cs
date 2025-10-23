namespace Miningcore.Api.Responses;

public class TransparencyInfo
{
    public string Id { get; set; }
    public string Coin { get; set; }
    public string Algorithm { get; set; }
    public string Name { get; set; }
    public string FeeType { get; set; }
    public ulong Hashrate { get; set; }
    public ulong NetworkHashrate { get; set; }
    public uint Miners { get; set; }
    public uint Workers { get; set; }
    public float Fee { get; set; }
    public ulong BlockHeight { get; set; }
}

public class GetTransparencyResponse
{
    public TransparencyInfo[] Pools { get; set; }
}

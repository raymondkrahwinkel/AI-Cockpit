using System.Security.Cryptography;

namespace Cockpit.Core.Swarm;

public static class SwarmRank
{
    public static long Draw(IEnumerable<long> occupied, Func<long>? next = null)
    {
        var ranks = occupied.ToHashSet();
        long rank;
        do
        {
            rank = next is null ? BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))) : next();
        }
        while (ranks.Contains(rank));
        return rank;
    }
}

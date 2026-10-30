using System.Security.Cryptography;

namespace Cockpit.Core.Swarm;

public static class SwarmRank
{
    public static long DrawAbove(IEnumerable<long> occupied)
    {
        var maximum = occupied.Max();
        if (maximum == long.MaxValue) throw new InvalidOperationException("No higher swarm rank is available.");
        if (maximum < 0)
        {
            long rank;
            do { rank = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(sizeof(long))); } while (rank <= maximum);
            return rank;
        }
        var offset = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(long))) % (ulong)(long.MaxValue - maximum);
        return maximum + (long)offset + 1;
    }

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

using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

internal static class SwarmRankTests
{
    internal static void CollisionIsRetriedBeforeSigning()
    {
        foreach (var taken in new[] { 1, long.MinValue, long.MaxValue })
        {
            var values = new Queue<long>([taken, taken, 42]);
            Assert.Equal(42, SwarmRank.Draw([taken], values.Dequeue));
            Assert.Empty(values);
        }
    }
}

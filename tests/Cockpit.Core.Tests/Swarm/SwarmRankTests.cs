using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

public class SwarmRankTests
{
    [Theory]
    [MemberData(nameof(Ranks))]
    public void CollisionIsRetriedBeforeSigning(long taken)
    {
        var values = new Queue<long>([taken, taken, 42]);
        Assert.Equal(42, SwarmRank.Draw([taken], values.Dequeue));
        Assert.Empty(values);
    }

    public static TheoryData<long> Ranks => new() { 1, long.MinValue, long.MaxValue };
}

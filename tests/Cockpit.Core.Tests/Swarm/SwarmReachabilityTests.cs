using System.Net;
using Cockpit.Core.Swarm;

namespace Cockpit.Core.Tests.Swarm;

public class SwarmReachabilityTests
{
    public static TheoryData<HttpStatusCode> Responses => new() { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden };

    [Theory]
    [MemberData(nameof(Responses))]
    public void OnlyAuthorizedAnswersRenewTheLeaseAndStableReturn(HttpStatusCode response)
    {
        var time = new FakeTimeProvider();
        var service = new SwarmReachability(time);
        var member = SwarmMemberId.FromPublicKey([1, 2, 3]);
        service.Answer(member, HttpStatusCode.OK);
        var first = service.Get(member).LastSeen;
        time.Advance(10);
        service.Answer(member, response);
        time.Advance(5);
        var live = service.Get(member);
        Assert.Equal(response == HttpStatusCode.OK ? SwarmPresence.Online : SwarmPresence.NotReachable, live.Status);
        Assert.Equal(response == HttpStatusCode.OK ? first.GetValueOrDefault().AddSeconds(10) : first, live.LastSeen);
        Assert.False(live.Stable);
        time.Advance(15);
        service.Answer(member, HttpStatusCode.OK);
        Assert.False(service.Get(member).Stable);
        for (var i = 0; i < 6; i++)
        {
            time.Advance(5);
            service.Answer(member, HttpStatusCode.OK);
        }
        Assert.True(service.Get(member).Stable);
        service.Answer(member, HttpStatusCode.OK, asleep: true);
        Assert.Equal(SwarmPresence.Asleep, service.Get(member).Status);
        Assert.False(service.Get(member).Stable);
        service.Answer(member, HttpStatusCode.OK);
        Assert.False(service.Get(member).Stable);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}

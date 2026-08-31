namespace Cockpit.Core.Tests;

public sealed class Ac1268NightlyFailureProofTests
{
    [Fact]
    public void DeliberateFailureMakesNightlyRed()
    {
        Assert.Fail("AC-1268 disposable nightly failure proof");
    }
}

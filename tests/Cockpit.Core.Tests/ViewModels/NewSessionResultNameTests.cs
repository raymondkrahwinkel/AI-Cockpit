using Cockpit.App.ViewModels;
using Cockpit.Core.Profiles;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Which starting sessions carry a name somebody meant (#AC-310, #AC-324). A ticket linked to a session later takes
/// a name nobody chose and leaves one that was chosen, so this is the line between "still labelable" and "hands off".
/// It used to be worked out at each start route, after the session was already up — three routes put the flag back
/// and the fourth forgot, which is how a session started by a flow could never be relabelled. Now the result says it
/// and the cockpit applies it, so these cases are the rule rather than a description of it.
/// </summary>
public class NewSessionResultNameTests
{
    [Fact]
    public void ANameFromTheDialog_IsOneSomebodyChose() =>
        Assert.True(_Result("release work").NameIsChosen);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoNameAtAll_IsNobodys(string? name) =>
        Assert.False(_Result(name).NameIsChosen);

    // "Cockpit 2", "Claude — 14:22", "webshop (copy)": a start route putting a name together for itself is not the
    // operator naming it, however unlike a placeholder the result reads.
    [Fact]
    public void ANameAStartRouteComposed_IsNobodys() =>
        Assert.False((_Result("Cockpit 2") with { NameIsComposed = true }).NameIsChosen);

    // A copy is only as deliberate as what it was copied from, which is why the flag is passed rather than assumed.
    [Fact]
    public void ACopyOfADeliberateName_StaysDeliberate() =>
        Assert.True((_Result("release work (copy)") with { NameIsComposed = false }).NameIsChosen);

    private static NewSessionResult _Result(string? name) => new(
        SessionKind.Sdk,
        new SessionProfile("Claude", new ClaudeConfig("/config/dir")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort,
        name);
}

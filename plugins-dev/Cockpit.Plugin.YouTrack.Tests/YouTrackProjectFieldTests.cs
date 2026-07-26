using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The choices this plugin offers the project editor (AC-317). What is stored is the short name — the tag every
/// query it makes is written in — so the tests here are about the display text never leaking into the stored value,
/// and about an instance that could not be read saying so instead of looking empty.
/// </summary>
public class YouTrackProjectFieldTests
{
    private static YouTrackInstance Instance(string label) => new(label, $"https://{label}.test/api", "token", string.Empty);

    private static Func<YouTrackInstance, CancellationToken, Task<IReadOnlyList<YouTrackProject>>> Returns(
        params (string Instance, string ShortName, string Name)[] projects) =>
        (instance, _) => Task.FromResult<IReadOnlyList<YouTrackProject>>(
        [
            .. projects
                .Where(project => project.Instance == instance.Label)
                .Select(project => new YouTrackProject(project.ShortName, project.Name)),
        ]);

    [Fact]
    public async Task BuildOptions_NoConfiguredInstance_OffersNothingRatherThanFailing()
    {
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [new YouTrackInstance("Empty", string.Empty, string.Empty, string.Empty)],
            Returns(),
            CancellationToken.None);

        options.Should().BeEmpty("nothing is configured, which is not the same as something going wrong");
    }

    [Fact]
    public async Task BuildOptions_OneInstance_ReadsAsNameThenTagAndStoresTheTag()
    {
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [Instance("Personal")],
            Returns(("Personal", "AC", "AI-Cockpit")),
            CancellationToken.None);

        var option = options.Should().ContainSingle().Subject;
        option.Display.Should().Be("AI-Cockpit — AC");
        option.Value.Should().Be("AC");
    }

    [Fact]
    public async Task BuildOptions_AProjectWithNoName_FallsBackToItsTag()
    {
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [Instance("Personal")],
            Returns(("Personal", "AC", string.Empty)),
            CancellationToken.None);

        options.Single().Display.Should().Be("AC");
    }

    [Fact]
    public async Task BuildOptions_OneInstance_DoesNotPrefixWithTheInstanceLabel()
    {
        // "Which YouTrack" is a question a single-instance cockpit never asks, and answering it anyway makes every
        // choice longer for nothing.
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [Instance("Personal")],
            Returns(("Personal", "AC", "AI-Cockpit")),
            CancellationToken.None);

        options.Single().Display.Should().NotContain("Personal");
    }

    [Fact]
    public async Task BuildOptions_SeveralInstances_SaysWhichOneEachProjectIsOn()
    {
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [Instance("Personal"), Instance("Work")],
            Returns(("Personal", "AC", "AI-Cockpit"), ("Work", "PAY", "Payroll")),
            CancellationToken.None);

        options.Select(option => option.Display).Should().Equal("Personal: AI-Cockpit — AC", "Work: Payroll — PAY");
    }

    [Fact]
    public async Task BuildOptions_TheSameTagOnTwoInstances_IsOfferedOnce()
    {
        // The link stores only the tag, so the second one would be a choice that saves as the first — two rows that
        // do the same thing, one of which is a lie about which server it points at.
        var options = await YouTrackProjectField.BuildOptionsAsync(
            [Instance("Personal"), Instance("Work")],
            Returns(("Personal", "AC", "AI-Cockpit"), ("Work", "AC", "Accounts")),
            CancellationToken.None);

        options.Should().ContainSingle().Which.Value.Should().Be("AC");
    }

    [Fact]
    public async Task BuildOptions_AConfiguredInstanceThatAnsweredWithNothing_ReportsItRatherThanLookingEmpty()
    {
        // GetProjectsAsync answers an unreachable instance and a token without admin read the same way it answers an
        // empty one. Passed on as "no projects", that reads as "you have none" — which it almost never means.
        var load = () => YouTrackProjectField.BuildOptionsAsync([Instance("Personal")], Returns(), CancellationToken.None);

        await load.Should().ThrowAsync<InvalidOperationException>()
            .Where(exception => exception.Message.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Key_IsTheOneAlreadyLinkedProjectsAreStoredUnder()
    {
        // Changing it silently unlinks every project that used it, and nothing else in the suite would notice.
        YouTrackProjectField.Key.Should().Be("youtrack.project");
    }
}

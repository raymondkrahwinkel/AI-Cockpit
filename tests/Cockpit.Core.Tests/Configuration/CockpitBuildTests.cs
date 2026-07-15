using Cockpit.Core.Configuration;
using FluentAssertions;

namespace Cockpit.Core.Tests.Configuration;

/// <summary>
/// A development build keeps its state beside the production one, never in it (AC-3).
/// </summary>
/// <remarks>
/// What cannot be tested here is <see cref="CockpitBuild.IsDevelopment"/> itself: it is a compile-time answer, so
/// a test run only ever compiles one arm of it and asserting its value would only restate which configuration the
/// suite was built in. What these tests do hold is the half that matters and that a wrong edit could quietly
/// break — that the two roots are actually different, and that the production one keeps the name the operator's
/// existing state is already under.
/// </remarks>
public sealed class CockpitBuildTests
{
    [Fact]
    public void StateFolders_ForDevelopmentAndProduction_AreDifferentDirectories()
    {
        CockpitBuild.DevelopmentStateFolder.Should().NotBe(CockpitBuild.ProductionStateFolder);
    }

    [Fact]
    public void ProductionStateFolder_IsTheNameTheOperatorsStateIsAlreadyUnder()
    {
        CockpitBuild.ProductionStateFolder.Should().Be("Cockpit",
            "renaming this orphans every installed cockpit's settings, plugins and profiles");
    }

    [Fact]
    public void StateRoot_IsTheStateFolderUnderTheApplicationDataDirectory()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), CockpitBuild.StateFolder);

        CockpitBuild.StateRoot.Should().Be(expected);
    }
}

using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Which project fields the editor ends up drawing (AC-317). Two plugins registering the same key is the agreed
/// case — a repository is a repository, and either of the GitHub plugins alone must still offer the field — so the
/// registry's job is to keep exactly one of them rather than to complain.
/// </summary>
public class ProjectFieldRegistryTests
{
    private static ProjectFieldRegistration Field(string key, string title) =>
        new(key, title, _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>([]));

    [Fact]
    public void Register_TwoPluginsOfferingTheSameKey_KeepsTheFirst()
    {
        var registry = new ProjectFieldRegistry();

        registry.Register(Field("github.repository", "GitHub repository")).Should().BeTrue();
        registry.Register(Field("github.repository", "Repository")).Should().BeFalse();

        registry.Fields.Should().ContainSingle().Which.Title.Should().Be("GitHub repository");
    }

    [Fact]
    public void Register_KeysDifferingOnlyInCase_AreDifferentFields()
    {
        // A link is read back case-sensitively (Project.LinkedAs). A registry that folded case here would hand the
        // editor one field whose saved value the other plugin could never find.
        var registry = new ProjectFieldRegistry();

        registry.Register(Field("github.repository", "GitHub repository")).Should().BeTrue();
        registry.Register(Field("GitHub.Repository", "Repository")).Should().BeTrue();

        registry.Fields.Should().HaveCount(2);
    }

    [Fact]
    public void Register_AFieldWithNoKey_IsRefused()
    {
        // There is nothing to store it under, so the field would draw a box whose value went nowhere.
        var registry = new ProjectFieldRegistry();

        registry.Register(Field("  ", "Nameless")).Should().BeFalse();

        registry.Fields.Should().BeEmpty();
    }

    [Fact]
    public void TheAppsOwnScan_ResolvesTheRegistry()
    {
        // The project editor takes IProjectFieldRegistry as a constructor dependency, so a missing marker interface
        // is not a quiet degradation — it is the app failing to start. Nothing else here would notice: every other
        // test in this suite builds the registry with new().
        var services = new ServiceCollection();
        services.AddServices(typeof(ProjectFieldRegistry).Assembly);

        services.BuildServiceProvider().GetService<IProjectFieldRegistry>().Should().BeOfType<ProjectFieldRegistry>();
    }

    [Fact]
    public void Fields_AreOfferedInRegistrationOrder()
    {
        var registry = new ProjectFieldRegistry();
        registry.Register(Field("youtrack.project", "YouTrack project"));
        registry.Register(Field("github.repository", "GitHub repository"));

        registry.Fields.Select(field => field.Key).Should().Equal("youtrack.project", "github.repository");
    }
}

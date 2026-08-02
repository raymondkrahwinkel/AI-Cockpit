using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Which project a plugin's ownership claim resolves to (AC-604). Two plugins claiming the same project is the
/// same "agreement, not a clash" rule <see cref="ProjectFieldRegistryTests"/> already covers for a shared field
/// key — first registration wins, second is refused, nothing throws.
/// </summary>
public class ProjectOwnershipRegistryTests
{
    [Fact]
    public void Register_TwoPluginsClaimingTheSameProject_KeepsTheFirst()
    {
        var registry = new ProjectOwnershipRegistry();

        Assert.True(registry.Register(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work"))));
        Assert.False(registry.Register(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Another plugin"))));

        var resolved = registry.Resolve("proj-1");
        Assert.Equal("Depot — Work", resolved![HostProjectField.Name]!.SourceName);
    }

    [Fact]
    public void Register_AnEmptyProjectId_IsRefused()
    {
        var registry = new ProjectOwnershipRegistry();

        Assert.False(registry.Register(new ProjectOwnershipRegistration("  ")));
    }

    [Fact]
    public void Resolve_AProjectNoOneClaimed_IsNull()
    {
        // AC-604 acceptance criterion 4: an unclaimed project resolves null, not an empty dictionary — the editor
        // reads null as "draw exactly as always", which an empty-but-non-null dictionary would not signal the same way.
        var registry = new ProjectOwnershipRegistry();
        registry.Register(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work")));

        Assert.Null(registry.Resolve("proj-vanished"));
    }

    [Fact]
    public void Resolve_AProjectWideDefault_ClaimsEveryField()
    {
        var registry = new ProjectOwnershipRegistry();
        registry.Register(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work", IsEditable: true)));

        var resolved = registry.Resolve("proj-1")!;

        Assert.All(Enum.GetValues<HostProjectField>(), field =>
        {
            Assert.NotNull(resolved[field]);
            Assert.Equal("Depot — Work", resolved[field]!.SourceName);
            Assert.True(resolved[field]!.IsEditable);
        });
    }

    [Fact]
    public void Resolve_APerFieldOverride_WinsOverTheProjectWideDefault()
    {
        // The mixed case the ticket names: name and behaviour shared, the folder-adjacent worktree switch stays local.
        var registry = new ProjectOwnershipRegistry();
        registry.Register(new ProjectOwnershipRegistration("proj-1", new ProjectFieldOwnership("Depot — Work"))
        {
            Overrides = new Dictionary<HostProjectField, ProjectFieldOwnership?>
            {
                [HostProjectField.WorktreeSwitch] = null,
            },
        });

        var resolved = registry.Resolve("proj-1")!;

        Assert.NotNull(resolved[HostProjectField.Name]);
        Assert.Null(resolved[HostProjectField.WorktreeSwitch]);
    }

    [Fact]
    public void Resolve_ANullProjectWideDefaultWithOneOverride_ClaimsOnlyThatField()
    {
        // A plugin that shares only Name, leaving Default null rather than opting the other five out one at a time.
        var registry = new ProjectOwnershipRegistry();
        registry.Register(new ProjectOwnershipRegistration("proj-1")
        {
            Overrides = new Dictionary<HostProjectField, ProjectFieldOwnership?>
            {
                [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work"),
            },
        });

        var resolved = registry.Resolve("proj-1")!;

        Assert.NotNull(resolved[HostProjectField.Name]);
        Assert.Null(resolved[HostProjectField.Description]);
        Assert.Null(resolved[HostProjectField.Logo]);
        Assert.Null(resolved[HostProjectField.Behavior]);
        Assert.Null(resolved[HostProjectField.McpOverlay]);
        Assert.Null(resolved[HostProjectField.WorktreeSwitch]);
    }
}

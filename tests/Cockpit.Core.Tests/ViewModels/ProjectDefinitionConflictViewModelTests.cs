using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="ProjectDefinitionConflictViewModel"/>'s row-building (AC-247, mockup section 6): which fields the
/// conflict window shows, and which of those it flags as a genuine two-sided collision.
/// </summary>
public class ProjectDefinitionConflictViewModelTests
{
    private static SharedProjectBinding Binding(string name = "Cockpit", string? description = null, string? behaviorPrompt = null) =>
        new(name) { Description = description, BehaviorPrompt = behaviorPrompt };

    private static SharedProjectDefinitionEdit Edit(string name = "Cockpit", string? description = null, string? behaviorPrompt = null) =>
        new(name, description, behaviorPrompt, IsolateInWorktreeByDefault: false, EnabledMcpServerNames: null);

    [Fact]
    public void Rows_OnlyTheRemoteSideChanged_IsARowButNotACollision_BothSidesChangedIs()
    {
        var baseline = Binding(name: "Cockpit", description: "Original");
        var mine = Edit(name: "Cockpit", description: "Original"); // the operator never touched this field.
        var latest = Binding(name: "Cockpit", description: "Someone else's edit");

        var viewModel = new ProjectDefinitionConflictViewModel(mine, baseline, latest);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("Description", row.FieldLabel);
        Assert.False(row.MineChanged);
        Assert.Equal("unchanged", row.MineValue);
        Assert.False(viewModel.HasCollision);

        // Both sides changed the same field: that is a collision.
        var collision = new ProjectDefinitionConflictViewModel(
            Edit(behaviorPrompt: "Use ICqrsSender + no AutoMapper"),
            Binding(behaviorPrompt: "Use ICqrsSender"),
            Binding(behaviorPrompt: "Use ICqrsSender, add tests"));

        var both = Assert.Single(collision.Rows);
        Assert.Equal("Behaviour", both.FieldLabel);
        Assert.True(both.MineChanged);
        Assert.Equal("Use ICqrsSender + no AutoMapper", both.MineValue);
        Assert.Equal("Use ICqrsSender, add tests", both.DepotValue);
        Assert.True(collision.HasCollision);
    }
}

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
    public void Rows_AFieldThatNeverMovedRemotely_HasNoRow()
    {
        var baseline = Binding(name: "Cockpit", description: "Original");
        var mine = Edit(name: "Cockpit", description: "My edit");
        var latest = Binding(name: "Cockpit", description: "Original"); // remote never touched Description either.

        var viewModel = new ProjectDefinitionConflictViewModel(mine, baseline, latest);

        Assert.DoesNotContain(viewModel.Rows, row => row.FieldLabel == "Description");
    }

    [Fact]
    public void Rows_OnlyTheRemoteSideChanged_IsARowButNotACollision()
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
    }

    [Fact]
    public void Rows_BothSidesChangedTheSameField_IsACollision()
    {
        var baseline = Binding(behaviorPrompt: "Use ICqrsSender");
        var mine = Edit(behaviorPrompt: "Use ICqrsSender + no AutoMapper");
        var latest = Binding(behaviorPrompt: "Use ICqrsSender, add tests");

        var viewModel = new ProjectDefinitionConflictViewModel(mine, baseline, latest);

        var row = Assert.Single(viewModel.Rows);
        Assert.Equal("Behaviour", row.FieldLabel);
        Assert.True(row.MineChanged);
        Assert.Equal("Use ICqrsSender + no AutoMapper", row.MineValue);
        Assert.Equal("Use ICqrsSender, add tests", row.DepotValue);
        Assert.True(viewModel.HasCollision);
    }

    [Fact]
    public void Rows_OnlyTheOperatorChangedIt_AndTheRemoteStayedPut_HasNoRowAtAll()
    {
        // Nothing to reconcile: this field alone would never have conflicted, so it must not appear as if it did.
        var baseline = Binding(name: "Cockpit");
        var mine = Edit(name: "Renamed by me");
        var latest = Binding(name: "Cockpit");

        var viewModel = new ProjectDefinitionConflictViewModel(mine, baseline, latest);

        Assert.Empty(viewModel.Rows);
        Assert.False(viewModel.HasCollision);
    }

}

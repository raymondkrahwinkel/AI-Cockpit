using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Tests.Shared;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The @-mention list's state machine: groups in the window's order, filtering across them, arrow keys that skip the
/// headings, Esc that keeps the list shut for its token, and accepted tokens that survive names with spaces.
/// </summary>
[Collection("avalonia")]
public class MentionPickerViewModelTests
{
    [Fact]
    public void FilterOverTwoGroups_SkipsHeadings_StaysShutAfterEsc_AndKeepsSpacedTokens()
    {
        var viewModel = new MentionPickerViewModel(
            [
                new FixedMentionSource(10, new MentionGroup("Projects on this PC", [FixedMentionSource.Project("EVE Workbench"), FixedMentionSource.Project("Foo App")])),
                new FixedMentionSource(0, new MentionGroup("Files in repo", [FixedMentionSource.File("src/Foo.cs")])),
            ],
            () => new MentionContext(MentionSurface.Session, "/repo"));

        viewModel.OnTextChanged("@fo", 3);

        Assert.Equal(["Files in repo", "Foo.cs", "Projects on this PC", "Foo App"], viewModel.Matches.Select(match => match.IsHeading ? match.Heading : match.Item!.Title));
        viewModel.Move(1);
        Assert.Equal("Foo App", viewModel.Selected!.Item!.Title);
        viewModel.Move(-1);
        Assert.Equal("Foo.cs", viewModel.Selected!.Item!.Title);

        viewModel.Dismiss();
        viewModel.OnTextChanged("@foo", 4);
        Assert.False(viewModel.IsOpen);
        viewModel.OnTextChanged("@foo @", 6);
        Assert.True(viewModel.IsOpen);

        viewModel.OnTextChanged("@eve", 4);
        var acceptance = viewModel.Accept()!;
        Assert.Equal("EVE Workbench", acceptance.Item.InsertText);
        viewModel.SyncTokens("@EVE Workbench ");
        Assert.Equal("EVE Workbench", viewModel.FirstProjectToken!.Item.Title);
        viewModel.SyncTokens("@EVE Workben");
        Assert.Null(viewModel.FirstProjectToken);
    }
}

using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The command palette's filtering and selection (#: command palette): case-insensitive title filter, the top
/// match auto-selected, arrow-key movement clamped to the list, and Run exposing the chosen command's action.
/// </summary>
public class CommandPaletteDialogViewModelTests
{
    private static PaletteCommand Cmd(string title, Action? invoke = null) =>
        new(title, string.Empty, invoke ?? (() => { }));

    [Fact]
    public void Filter_IsCaseInsensitiveOnTitle_AndSelectsTheTopMatch()
    {
        var vm = new CommandPaletteDialogViewModel([Cmd("New session"), Cmd("Open options"), Cmd("Search transcripts")]);

        vm.Query = "se";

        vm.Visible.Select(c => c.Title).Should().Equal("New session", "Search transcripts");
        vm.Selected!.Title.Should().Be("New session");
    }

    [Fact]
    public void Move_ClampsWithinTheVisibleList()
    {
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("B"), Cmd("C")]);

        vm.Move(-1);
        vm.Selected!.Title.Should().Be("A");

        vm.Move(1);
        vm.Move(1);
        vm.Move(1);
        vm.Selected!.Title.Should().Be("C");
    }

    [Fact]
    public void Run_ExposesTheSelectedCommandsActionAsChosen()
    {
        var ran = 0;
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("Run me", () => ran++)]);
        vm.Query = "run";

        vm.RunCommand.Execute(null);
        vm.Chosen!.Invoke();

        ran.Should().Be(1);
    }

    [Fact]
    public void EmptyQuery_ShowsEverything()
    {
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("B")]);

        vm.Visible.Should().HaveCount(2);
    }
}

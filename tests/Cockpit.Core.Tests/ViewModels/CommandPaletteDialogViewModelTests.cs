using Cockpit.App.ViewModels;

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

        Assert.Equal(new[] { "New session", "Search transcripts" }, vm.Visible.Select(c => c.Title));
        Assert.Equal("New session", vm.Selected!.Title);
    }

    [Fact]
    public void Move_ClampsWithinTheVisibleList()
    {
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("B"), Cmd("C")]);

        vm.Move(-1);
        Assert.Equal("A", vm.Selected!.Title);

        vm.Move(1);
        vm.Move(1);
        vm.Move(1);
        Assert.Equal("C", vm.Selected!.Title);
    }

    [Fact]
    public void Run_ExposesTheSelectedCommandsActionAsChosen()
    {
        var ran = 0;
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("Run me", () => ran++)]);
        vm.Query = "run";

        vm.RunCommand.Execute(null);
        vm.Chosen!.Invoke();

        Assert.Equal(1, ran);
    }

    [Fact]
    public void EmptyQuery_ShowsEverything()
    {
        var vm = new CommandPaletteDialogViewModel([Cmd("A"), Cmd("B")]);

        Assert.Equal(2, System.Linq.Enumerable.Count(vm.Visible));
    }
}

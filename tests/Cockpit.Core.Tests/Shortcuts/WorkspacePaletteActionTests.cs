using Cockpit.App.ViewModels;
using Cockpit.Core.Shortcuts;

namespace Cockpit.Core.Tests.Shortcuts;

/// <summary>
/// The workspace and widget commands in the palette (F5). The palette lists every command whether or not it
/// applies right now, so each has to be a no-op when it does not rather than a surprise — and the ones that
/// stop running sessions must not get there without the same prompt the tab's ✕ shows.
/// </summary>
public class WorkspacePaletteActionTests
{
    /// <summary>
    /// Unbound by default, all of them: every default gesture handed out is one taken from the shell underneath,
    /// and these are things you do to a workspace once and then live with. The palette is where they are found.
    /// </summary>
    [Theory]
    [InlineData(ShortcutAction.NewSessionsWorkspace)]
    [InlineData(ShortcutAction.NewDashboardWorkspace)]
    [InlineData(ShortcutAction.CloseWorkspace)]
    public void TheWorkspaceActions_AreInTheCatalogAndUnboundByDefault(ShortcutAction action)
    {
        var descriptor = Assert.Single(ShortcutCatalog.All, entry => entry.Action == action);

        Assert.Empty(descriptor.DefaultGesture);
        Assert.NotEmpty(descriptor.Label);
    }

    /// <summary>They open dialogs and change what is on screen, so they stay out of the way while a TUI has the keyboard.</summary>
    [Theory]
    [InlineData(ShortcutAction.NewSessionsWorkspace)]
    [InlineData(ShortcutAction.NewDashboardWorkspace)]
    [InlineData(ShortcutAction.CloseWorkspace)]
    public void TheWorkspaceActions_DoNotStayActiveInATerminal(ShortcutAction action)
    {
        Assert.False(ShortcutCatalog.StaysActiveInTerminal(action));
    }

    /// <summary>Every action in the catalog has to be reachable, or it is a row in Options that does nothing.</summary>
    [Fact]
    public void EveryCatalogAction_HasADescriptor()
    {
        Assert.Equivalent(
            Enum.GetValues<ShortcutAction>(),
            ShortcutCatalog.All.Select(descriptor => descriptor.Action));
    }

    /// <summary>Every workspace command reaches the palette — that is the only place they can be found at all, being unbound.</summary>
    [Fact]
    public void ThePalette_OffersTheWorkspaceCommands()
    {
        var titles = new CockpitViewModel().BuildPaletteCommands().Select(command => command.Title).ToList();

        Assert.All(
            new[] { "New sessions workspace", "New dashboard workspace", "Close workspace" },
            title => Assert.Contains(title, titles));
    }

    /// <summary>The palette opens itself; listing it inside itself would be a row that does nothing you did not just do.</summary>
    [Fact]
    public void ThePalette_DoesNotOfferItself()
    {
        Assert.DoesNotContain(
            new CockpitViewModel().BuildPaletteCommands(),
            command => command.Title == "Command palette");
    }

    /// <summary>
    /// A sessions workspace has nowhere to put a widget, so those commands are left out rather than listed and
    /// dead — a command you have to read before dismissing costs more than one that is not there.
    /// </summary>
    [Fact]
    public void OnASessionsWorkspace_ThePaletteOffersNoWidgets()
    {
        var vm = new CockpitViewModel();
        vm.Workspaces.EnsureSessionWorkspace();

        Assert.DoesNotContain(vm.BuildPaletteCommands(), command => command.Title.StartsWith("Add widget:"));
    }
}

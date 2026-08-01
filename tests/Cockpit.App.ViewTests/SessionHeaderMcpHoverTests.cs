using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-563: the MCP information moved to the hover that was asked for. The provider chip no longer opens a card
/// listing every tool the session connected — AC-537 had already ruled that total uninformative and taken it out
/// of the status line, and the card was the same figure one hover further along. The activity column, which
/// carried no tip at all, now lists the session's MCP servers by name.
/// <para>
/// Rendered rather than asserted on the view model, because both halves are claims about where a tip hangs: one
/// that a slot is left empty, one that a tip is on the column and not on the text inside it.
/// </para>
/// </summary>
[Collection("avalonia")]
public class SessionHeaderMcpHoverTests
{
    [Fact]
    public void TheProviderChipNoLongerOpensAToolsCard() => HeadlessAvalonia.Run(() =>
    {
        var window = _Show(new SessionView { DataContext = new SessionViewModel { KindLabel = "SDK" } });
        var tip = ToolTip.GetTip(_KindChip(window, "SDK"));
        window.Close();

        Assert.Null(tip);
    });

    /// <summary>
    /// Criterion 3: the slot itself stays, and the TTY header still fills it with its render diagnostics — the
    /// half of this ticket that is about not breaking the other route while clearing this one.
    /// </summary>
    [Fact]
    public void TheTtyChipKeepsItsOwnHoverContent() => HeadlessAvalonia.Run(() =>
    {
        var viewModel = TtyViewModel.DesignTerminal();
        var window = _Show(new TtyView { DataContext = viewModel });
        var tip = ToolTip.GetTip(_KindChip(window, viewModel.KindLabel!));
        window.Close();

        Assert.NotNull(tip);
    });

    /// <summary>
    /// Criteria 4 and 8: the names hang on the column, so they are reachable both when the column shows the
    /// session's own status line and when an agent's <c>set_status</c> has replaced it. A tip on either TextBlock
    /// would pass the first and fail the second — which is exactly backwards, since a session with a statusline
    /// is a session that is working.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("AC-563 — wiring the header hover")]
    public void TheActivityColumnHoversTheSessionsMcpServers(string statusline) => HeadlessAvalonia.Run(() =>
    {
        var viewModel = new SessionViewModel
        {
            KindLabel = "SDK",
            Statusline = statusline,
            McpServerSelection = new HashSet<string>(StringComparer.Ordinal) { "youtrack", "filesystem" },
        };

        var window = _Show(new SessionView { DataContext = viewModel });
        var column = window.GetVisualDescendants().OfType<SessionHeaderBar>().Single()
            .GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "ActivityColumn");
        var tip = ToolTip.GetTip(column) as string;
        window.Close();

        Assert.NotNull(tip);
        Assert.Contains("filesystem", tip, StringComparison.Ordinal);
        Assert.Contains("youtrack", tip, StringComparison.Ordinal);
    });

    // By the label it renders, not by "the first tag-classed border" — the worktree and claim-collision chips
    // share that class and one of them carries a tooltip of its own, which is what a looser lookup found first.
    private static Border _KindChip(Window window, string label) =>
        window.GetVisualDescendants().OfType<SessionHeaderBar>().Single()
            .GetVisualDescendants().OfType<Border>()
            .Single(b => b.Child is TextBlock text && text.Text == label);

    private static Window _Show(Control content)
    {
        var window = new Window { Width = 1000, Height = 400, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        return window;
    }
}

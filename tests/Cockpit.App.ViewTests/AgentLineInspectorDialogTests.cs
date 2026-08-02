using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The operator's window on the agent line (AC-397), against the real markup. Rendered rather than only asserted on
/// the view model, because that is where this kind of window goes wrong: a section bound to the wrong collection, a
/// row template that trims a pane id to nothing, an empty state that draws on top of a populated list.
/// <para>
/// The one property this window must never lose is that it is read-only. That is checked here by counting what it
/// can do at all: two buttons, Refresh and Close. A window that could reply, wake or release from this list would
/// put the operator inside the path it exists to show them from outside.
/// </para>
/// </summary>
[Collection("avalonia")]
public class AgentLineInspectorDialogTests
{
    private static AgentLineInspectorViewModel _Populated()
    {
        var inspector = new AgentLineInspectorViewModel();
        inspector.Messages.Add(new AgentLineMessageRow("09:12:03", "pane-a", "pane-b", "heads-up", "Accepted", "I am merging DEP-85 to dev"));
        inspector.Messages.Add(new AgentLineMessageRow("09:12:44", "pane-a", "pane-b", "heads-up", "RefusedRateLimited", "and again"));
        inspector.Wakes.Add(new AgentLineWakeRow("09:12:03", "pane-a", "pane-b", "Woken"));
        inspector.Claims.Add(new AgentLineClaimRow("/repo/worktree-a", "pane-b", "38 min"));
        inspector.Budget.Add(new AgentLineBudgetRow("pane-a", "Message", "20 of 20 in the last 60s"));
        inspector.Gaps.Add(new AgentLineGapRow("pane-c", "On this desk, but has never called a cockpit-agents tool."));
        inspector.DeskNote = "Desk ws-1 · 3 agent session(s)";
        inspector.EmptyNote = string.Empty;
        return inspector;
    }

    [Fact]
    public void APopulatedLine_RendersEverySection() => HeadlessAvalonia.Run(() =>
    {
        var window = new AgentLineInspectorDialog { DataContext = _Populated() };
        window.Show();
        window.UpdateLayout();

        var text = string.Join(
            "\n",
            window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).Where(line => !string.IsNullOrEmpty(line)));
        window.Close();

        // One line from each of the five sources the ticket requires, so a section bound to the wrong collection
        // fails here rather than showing up as a quietly missing panel.
        Assert.Contains("I am merging DEP-85 to dev", text, StringComparison.Ordinal);
        Assert.Contains("Woken", text, StringComparison.Ordinal);
        Assert.Contains("/repo/worktree-a", text, StringComparison.Ordinal);
        Assert.Contains("20 of 20", text, StringComparison.Ordinal);
        Assert.Contains("pane-c", text, StringComparison.Ordinal);
        // A refusal is shown next to the sends, not filtered out of them: a stream of refusals is the thing an
        // operator is looking for, and a list of only what succeeded would not show it.
        Assert.Contains("RefusedRateLimited", text, StringComparison.Ordinal);
    });

    [Fact]
    public void AnEmptyLine_LooksEmptyRatherThanBroken() => HeadlessAvalonia.Run(() =>
    {
        var window = new AgentLineInspectorDialog { DataContext = new AgentLineInspectorViewModel() };
        window.Show();
        window.UpdateLayout();

        var text = string.Join(
            "\n",
            window.GetVisualDescendants().OfType<TextBlock>()
                .Where(block => block.IsEffectivelyVisible)
                .Select(block => block.Text));
        window.Close();

        Assert.Contains("Select a session", text, StringComparison.Ordinal);
    });

    /// <summary>
    /// Read-only, checked by what the window offers rather than by trusting the markup to stay that way: exactly two
    /// buttons, and neither of them acts on the line.
    /// </summary>
    [Fact]
    public void TheWindowOffersNothingThatActsOnTheLine() => HeadlessAvalonia.Run(() =>
    {
        var window = new AgentLineInspectorDialog { DataContext = _Populated() };
        window.Show();
        window.UpdateLayout();

        // Every button that offers something in words. The window chrome's caption buttons are icon-only and drop
        // out here, which is what makes this a check on the window's own affordances rather than on its frame.
        var buttons = window.GetVisualDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible)
            .Select(button => (button.Content as string) ?? _LabelOf(button))
            .Where(label => !string.IsNullOrEmpty(label))
            .ToArray();
        window.Close();

        Assert.Equal(["Refresh", "Close"], [.. buttons.Order(StringComparer.Ordinal).Reverse()]);

        static string _LabelOf(Button button) =>
            button.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text).FirstOrDefault() ?? string.Empty;
    });
}

using Cockpit.Plugin.Browser.UI;

namespace Cockpit.Plugin.Browser.Tests;

// AC-1875, View layer: what the Agents side does with a tab an agent put in front of the operator.
public class AgentOperatorTabViewTests
{
    private static AgentTab Tab(string pane, string target, bool retained = false, bool requested = false) =>
        new(pane, "Zyra", target, "Mockup", "http://localhost:5173/mockup", retained, Retained: retained, Requested: requested);

    // View, one scenario: a self-open (a refresh without a show) keeps the operator's selection and asks for nothing;
    // a show for the operator tab selects exactly that tab and asks the shell to come forward, with its origin; and
    // once the session stops the same tab is labelled stopped and the operator can close it (the close targets it).
    [Fact]
    public void OperatorTab_IsShownWithItsOrigin_SelfOpenStealsNoFocus_AndARetainedTabIsLabelledStopped()
    {
        var model = new AgentTabsModel(null);
        var forward = 0;
        model.ShowRequested += () => forward++;
        model.Apply([Tab("pane-1", "self"), Tab("pane-2", "mockup", requested: true)]);
        model.Apply([Tab("pane-1", "self"), Tab("pane-2", "mockup", requested: true), Tab("pane-1", "self-2")]);
        Assert.Equal("self", model.Active?.TargetId);
        Assert.Equal(0, forward);

        model.RequestShow("pane-2", "mockup");
        model.Apply([Tab("pane-1", "self"), Tab("pane-2", "mockup", requested: true)]);
        Assert.Equal(("pane-2", "mockup"), (model.Active?.PaneId, model.Active?.TargetId));
        Assert.Equal("Opened by Zyra", model.Active?.Origin);
        Assert.Equal(1, forward);

        model.Apply([Tab("retained:1", "mockup", retained: true, requested: true)]);
        Assert.Equal("Opened by Zyra · session stopped", model.Active?.Origin);
        Assert.True(model.Active?.Retained);
    }
}

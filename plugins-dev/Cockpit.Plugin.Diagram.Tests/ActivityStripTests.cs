using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Diagram.Tests;

[Collection("avalonia")]
public class ActivityStripTests
{
    private sealed class FakeSessions : ICockpitSessionObserver
    {
        public string? ActiveSessionWorkingDirectory => null;

        public event EventHandler? ActiveSessionChanged { add { } remove { } }

        public event EventHandler<SessionOutputText>? OutputProduced { add { } remove { } }

        public event EventHandler<SessionToolActivity>? ToolActivityObserved;

        public void Raise(SessionToolActivity activity) => ToolActivityObserved?.Invoke(this, activity);
    }

    private sealed class FakeHost : ICockpitHost
    {
        public FakeSessions FakeSessions { get; } = new();

        public IServiceProvider Services { get; } = new EmptyServiceProvider();

        public ICockpitActions Actions => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public ICockpitSessionObserver Sessions => FakeSessions;

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;

        private sealed class EmptyServiceProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }
    }

    private static string _Result(string id, string? changed = null, string? placed = null)
    {
        var json = new JsonObject { ["ok"] = true, ["id"] = id };
        if (changed is not null)
        {
            json["changed"] = changed;
        }

        if (placed is not null)
        {
            json["objectId"] = "obj-1";
            json["placed"] = placed;
        }

        return json.ToJsonString();
    }

    // A strip's ScrollViewer is a templated control — its content only joins the visual tree once the template
    // applies, which needs a rooted window (same reason DiagramCollabWindowTests shows its content before
    // walking GetVisualDescendants).
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static List<string?> _Texts(Control content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

    [Fact]
    public void NoActivityYet_ShowsTheExplicitEmptyMessage_NeverABlankStrip()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);

        Assert.Contains("Deze sessie levert geen activiteit.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void MatchingToolActivity_OnThisSurfaceAndPane_AddsAReadableLine()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        host.FakeSessions.Raise(new SessionToolActivity(
            "pane-a", "mcp__cockpit-diagram__add_node", """{"id":"N1","label":"Foo"}""",
            _Result("surface-1", changed: "added node N1 \"Foo\""), IsError: false));

        var texts = _Texts(strip);
        Assert.Contains("added node N1 \"Foo\"", texts);
        Assert.DoesNotContain("Deze sessie levert geen activiteit.", texts);

        window.Close();
    }

    [Fact]
    public void ToolActivity_FromADifferentPane_IsIgnored()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        host.FakeSessions.Raise(new SessionToolActivity(
            "pane-b", "mcp__cockpit-diagram__add_node", """{"id":"N1","label":"Foo"}""",
            _Result("surface-1", changed: "added node N1 \"Foo\""), IsError: false));

        Assert.Contains("Deze sessie levert geen activiteit.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void ToolActivity_ForADifferentSurface_IsIgnored()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        host.FakeSessions.Raise(new SessionToolActivity(
            "pane-a", "mcp__cockpit-diagram__add_node", """{"id":"N1","label":"Foo"}""",
            _Result("surface-2", changed: "added node N1 \"Foo\""), IsError: false));

        Assert.Contains("Deze sessie levert geen activiteit.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void WhiteboardPlace_ProducesAReadableLine_FromThePlacedField()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "board-1", whiteboard: true, null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        host.FakeSessions.Raise(new SessionToolActivity(
            "pane-a", "mcp__cockpit-whiteboard__place_on_whiteboard", """{"whiteboard":"board-1","shape":"rectangle"}""",
            _Result("board-1", placed: "a rectangle reading \"Foo\""), IsError: false));

        Assert.Contains("placed a rectangle reading \"Foo\"", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void RevertButton_IsClickableAndNoOp_WithATooltipExplainingItIsNotYetAvailable()
    {
        var host = new FakeHost();
        var strip = new ActivityStrip(host, "surface-1", whiteboard: false, null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");
        host.FakeSessions.Raise(new SessionToolActivity(
            "pane-a", "mcp__cockpit-diagram__add_node", """{"id":"N1","label":"Foo"}""",
            _Result("surface-1", changed: "added node N1 \"Foo\""), IsError: false));

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Terugdraaien"));
        Assert.True(revert.IsEnabled);
        Assert.Contains("AC-853", (string)ToolTip.GetTip(revert)!);

        window.Close();
    }
}

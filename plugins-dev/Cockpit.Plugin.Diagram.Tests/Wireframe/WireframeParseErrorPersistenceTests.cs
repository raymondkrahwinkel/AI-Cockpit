using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Infrastructure.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe;
using Cockpit.Plugin.Diagram.Wireframe.Rendering;

namespace Cockpit.Plugin.Diagram.Tests.Wireframe;

// AC-976: a source that does not parse keeps saying so for as long as it does not parse — "+ Screen" adding a
// screen that itself parses fine is not the same as the broken lines getting fixed. A real WireframeAccessRegistry
// is used, the same reason WireframeStateChipTests does: AddScreen and UpdateText are its own line surgery.
[Collection("avalonia")]
public class WireframeParseErrorPersistenceTests
{
    private const string BrokenSource = "scherm \"Typo\"\n  knop \"Ok\"";

    [Fact]
    public void AddScreen_OnASourceThatDoesNotParse_KeepsTheErrorVisibleAndSaveBlocked()
    {
        var registry = new WireframeAccessRegistry();
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-1", "Typo", BrokenSource), sessionPaneId: null);

        var window = new Window { Content = body, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var addScreen = body.GetVisualDescendants().OfType<Button>().First(candidate => (string?)candidate.Content == "+ Screen");
        addScreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        // AC1: "New screen" now parses and renders, but the broken lines never went anywhere — the error the
        // operator already saw is still on screen, not just still in the source.
        Assert.Contains(
            body.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text?.Contains("Unknown component 'scherm'", StringComparison.Ordinal) == true);
        var source = body.GetVisualDescendants().OfType<TextBox>().First(candidate => candidate.Text?.Contains("scherm") == true);
        Assert.Contains("scherm \"Typo\"", source.Text);
        Assert.Contains("knop \"Ok\"", source.Text);

        // AC2: this is now the one path that used to write a broken source to disk without a word of warning.
        var save = body.GetVisualDescendants().OfType<Button>().First(candidate => (string?)candidate.Content == "Save");
        Assert.False(save.IsEnabled);

        window.Close();
    }

    [Fact]
    public void FixingTheBrokenLine_MakesTheErrorGoAwayAndUnblocksSave()
    {
        var registry = new WireframeAccessRegistry();
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-2", "Typo", BrokenSource), sessionPaneId: null);

        var window = new Window { Content = body, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        registry.UpdateText("wireframe-2", "screen \"Typo\"\n  button \"Ok\"");
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(
            body.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text?.Contains("Unknown component", StringComparison.Ordinal) == true);
        var save = body.GetVisualDescendants().OfType<Button>().First(candidate => (string?)candidate.Content == "Save");
        Assert.True(save.IsEnabled);

        window.Close();
    }

    [Fact]
    public void OneValidScreenBesideOneInvalidBlock_RendersAndShowsTheErrorTogether()
    {
        var registry = new WireframeAccessRegistry();
        var host = new ActivityStripTests.FakeHost(wireframe: registry);
        var source = "screen \"Login\"\n  button \"Ok\"\nscherm \"Typo\"";
        var body = new WireframeWorkspaceBody(host, new WireframeDocument("wireframe-3", "Login", source), sessionPaneId: null);

        var window = new Window { Content = body, Width = 1200, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains(
            body.GetVisualDescendants().OfType<Control>(),
            control => WireframeSource.GetNode(control)?.Text == "Ok");
        Assert.Contains(
            body.GetVisualDescendants().OfType<TextBlock>(),
            block => block.Text?.Contains("Unknown component 'scherm'", StringComparison.Ordinal) == true);

        window.Close();
    }
}

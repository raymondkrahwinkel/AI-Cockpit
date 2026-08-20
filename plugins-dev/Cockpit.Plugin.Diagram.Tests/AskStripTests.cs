using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Plugin.Diagram.Collab;

namespace Cockpit.Plugin.Diagram.Tests;

[Collection("avalonia")]
public class AskStripTests
{
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static List<string?> _Texts(Control content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

    private static void _RaiseClick(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    // AC-910 criterion 11: no fixed empty bar — the strip stays out of the layout until something has been asked.
    [Fact]
    public void NothingAskedYet_TheStripStaysHidden()
    {
        var strip = new AskStrip(null);
        var window = _Show(strip);

        Assert.False(strip.IsVisible);

        window.Close();
    }

    [Fact]
    public void AfterAsking_TheStripShowsTheQuestion()
    {
        var strip = new AskStrip(null);
        var window = _Show(strip);

        strip.Add("what should the agent do here?", "N1");
        Dispatcher.UIThread.RunJobs();

        Assert.True(strip.IsVisible);
        Assert.Contains("what should the agent do here?", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void HandledButton_OnAnOpenAsk_MarksItHandled_AndDisablesItself()
    {
        var strip = new AskStrip(null);
        var window = _Show(strip);
        strip.Add("why is this here?", "N1");
        Dispatcher.UIThread.RunJobs();

        var handled = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Handled"));
        Assert.True(handled.IsEnabled);

        _RaiseClick(handled);
        Dispatcher.UIThread.RunJobs();

        var reHandled = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Handled"));
        Assert.False(reHandled.IsEnabled);
        Assert.Contains(_Texts(strip), text => text is not null && text.Contains("handled", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void WithoutSelection_TheEntryHasNoObjectKey_AndStillShows()
    {
        var strip = new AskStrip(null);
        var window = _Show(strip);

        strip.Add("what should the agent do overall?", null);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("what should the agent do overall?", _Texts(strip));

        window.Close();
    }
}

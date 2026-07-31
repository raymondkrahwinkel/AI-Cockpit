using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-537: the kind chip can now carry a plugin-registered provider's own display name instead of a short
/// built-in label, so it needs the same trimming discipline as the session Title beside it — a different cap
/// (160, sized for the header's tag-sized real estate rather than the Title's 220), same mechanism
/// (<c>TextTrimming="CharacterEllipsis"</c>). Renders the real <see cref="SessionView"/>/<see cref="SessionHeaderBar"/>
/// XAML, not just the view model, since only a render proves the cap actually holds.
/// </summary>
[Collection("avalonia")]
public class SessionHeaderKindChipWidthTests
{
    [Fact]
    public void ALongPluginProviderName_TrimsToTheChipsCap_AndDoesNotWidenTheHeader() => HeadlessAvalonia.Run(() =>
    {
        var longName = new string('★', 500);

        var shortSession = new SessionViewModel { KindLabel = "SDK" };
        var shortWindow = new Window { Width = 1000, Height = 400, Content = new SessionView { DataContext = shortSession } };
        shortWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var headerWidthWithShortLabel = shortWindow.GetVisualDescendants().OfType<SessionHeaderBar>().Single().Bounds.Width;
        shortWindow.Close();

        var longSession = new SessionViewModel { KindLabel = longName };
        var longWindow = new Window { Width = 1000, Height = 400, Content = new SessionView { DataContext = longSession } };
        longWindow.Show();
        Dispatcher.UIThread.RunJobs();
        var header = longWindow.GetVisualDescendants().OfType<SessionHeaderBar>().Single();
        var chipText = header.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == longName);
        var headerWidthWithLongLabel = header.Bounds.Width;
        longWindow.Close();

        // Character-boundary ellipsis trimming stops at whichever glyph+"…" last fits, so the measured width lands
        // at or under the cap rather than exactly on it — this is not slack hiding a regression: if MaxWidth or
        // TextTrimming were ever removed, a 500-character string would measure to many times 160 and fail loudly.
        Assert.True(chipText.Bounds.Width <= 160d, $"expected the chip to trim to its 160 cap, measured {chipText.Bounds.Width}");
        Assert.Equal(headerWidthWithShortLabel, headerWidthWithLongLabel);
    });
}

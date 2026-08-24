using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Layout;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1063: at the sidebar's default (and minimum) 180px width, the card clips both the title — hard, no
/// ellipsis, no hint anything is missing — and the statusline, whose ~125px leaves room for only ~20-25
/// characters. The tooltip on the row is the only place either reads in full, and the same text is repeated
/// as AutomationProperties.HelpText so it is not hover-only.
/// </summary>
[Collection("avalonia")]
public class SessionSidebarCardTooltipTests
{
    private const string LongStatusline = "AC-1063 grooming tooltip + korte stand"; // 39 chars
    private const string LongTitle = "AC-1061 fase 5 HelmRunner and the rest"; // 39 chars

    private static (Window Window, Border Row) _BuildRow(SessionPanelViewModel session)
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();
        cockpit.Sessions.Add(session);
        cockpit.SidebarWidth = LayoutSettings.DefaultSidebarWidth;

        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 900, Height = 700 };
        window.Show();
        window.UpdateLayout();

        var strip = view.GetVisualDescendants().OfType<ItemsControl>().First(c => c.Name == "SessionListStrip");
        // DataContext is inherited, so every undeclared-DataContext descendant of the row (badges, labels) also
        // reports the session — the row itself is the one carrying the ContextMenu (AC-561's own row lookup).
        var row = strip.GetVisualDescendants().OfType<Border>()
            .Single(b => ReferenceEquals(b.DataContext, session) && b.ContextMenu is not null);

        return (window, row);
    }

    private static void _AssertFullTitleAndStatusline(SessionPanelViewModel session)
    {
        session.Title = LongTitle;
        session.Statusline = LongStatusline;

        var (window, row) = _BuildRow(session);
        var tip = ToolTip.GetTip(row) as string;
        var statuslineText = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == LongStatusline);
        var neededWidth = new FormattedText(LongStatusline, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface(statuslineText.FontFamily), statuslineText.FontSize, null).Width;
        window.Close();

        Assert.NotNull(tip);
        Assert.Contains(LongTitle, tip, StringComparison.Ordinal);
        Assert.Contains(LongStatusline, tip, StringComparison.Ordinal);
        Assert.True(statuslineText.Bounds.Width < neededWidth,
            $"card statusline width {statuslineText.Bounds.Width} must be narrower than the {neededWidth} the text needs, or nothing is actually clipped");
    }

    [Fact]
    public void HoverOnAnSdkCard_ShowsTheFullTitleAndStatusline_WhichTheCardItselfClips() =>
        HeadlessAvalonia.Run(() => _AssertFullTitleAndStatusline(new SessionViewModel()));

    [Fact]
    public void HoverOnATtyCard_ShowsTheFullTitleAndStatusline_WhichTheCardItselfClips() =>
        HeadlessAvalonia.Run(() => _AssertFullTitleAndStatusline(new TtyViewModel()));

    [Fact]
    public void EmptyStatusline_TooltipHasTheTitleOnly_NeverAnEmptyOrPlaceholderTip() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { Title = LongTitle, Statusline = string.Empty };
        var (window, row) = _BuildRow(session);

        var tip = ToolTip.GetTip(row);
        window.Close();

        Assert.Equal(LongTitle, tip);
        Assert.NotEqual(string.Empty, tip);
        Assert.DoesNotContain("—", (string)tip!, StringComparison.Ordinal);
        Assert.DoesNotContain("statusline", ((string)tip!).ToLowerInvariant(), StringComparison.Ordinal);
    });

    [Fact]
    public void ChangingTheStatusline_ChangesTheTooltipOnRead() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { Title = "Session X", Statusline = "first" };
        var (window, row) = _BuildRow(session);
        var before = ToolTip.GetTip(row) as string;

        session.Statusline = "second";
        var after = ToolTip.GetTip(row) as string;
        window.Close();

        Assert.Contains("first", before, StringComparison.Ordinal);
        Assert.DoesNotContain("second", before, StringComparison.Ordinal);
        Assert.Contains("second", after, StringComparison.Ordinal);
        Assert.DoesNotContain("first", after, StringComparison.Ordinal);
    });

    [Fact]
    public void TheSameTooltipText_IsAlsoReachableWithoutHover_ViaAutomationHelpText() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel { Title = LongTitle, Statusline = LongStatusline };
        var (window, row) = _BuildRow(session);

        var tip = ToolTip.GetTip(row) as string;
        var helpText = AutomationProperties.GetHelpText(row);
        window.Close();

        Assert.Equal(tip, helpText);
    });
}

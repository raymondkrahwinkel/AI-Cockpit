using Avalonia.Controls;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-65: the selection has to follow keyboard focus, not only a pointer press, or a terminal can hold focus
/// while the selected session sits on another pane — or on nothing — and the F9 voice hold, text injection and
/// the accent border all miss the pane the operator is in. Both the click handler and the new GotFocus handler
/// find the pane an element belongs to through <see cref="CockpitView._PaneContainerFromSource"/>: the walk up
/// to the child sitting directly in the tile panel. These pin that walk, since it is the piece both paths lean on
/// and the one a visual-tree change would break silently.
/// </summary>
[Collection("avalonia")]
public class PaneFocusSelectionTests
{
    [Fact]
    public void AnElementDeepInsideAPane_ResolvesToThatPanesContainerAndSession() => HeadlessAvalonia.Run(() =>
    {
        var sessionA = new SessionViewModel();
        var sessionB = new SessionViewModel();

        // A control nested a couple of levels down in pane B, as a focused terminal or header button would be.
        var deepInB = new Button();
        var paneA = new Border { DataContext = sessionA, Child = new Decorator() };
        var paneB = new Border { DataContext = sessionB, Child = new Decorator { Child = deepInB } };

        var panel = new SessionTilePanel();
        panel.Children.Add(paneA);
        panel.Children.Add(paneB);

        var window = new Window { Content = panel, Width = 400, Height = 300 };
        window.Show();
        window.UpdateLayout();

        var container = CockpitView._PaneContainerFromSource(deepInB);

        container.Should().BeSameAs(paneB, "the walk stops at the pane sitting directly in the tile panel");
        container!.DataContext.Should().BeSameAs(sessionB, "so the focused element's pane carries its own session");
    });

    [Fact]
    public void AnElementOutsideAnyPane_ResolvesToNothing() => HeadlessAvalonia.Run(() =>
    {
        var loose = new Button();
        var window = new Window { Content = new Decorator { Child = loose }, Width = 200, Height = 100 };
        window.Show();
        window.UpdateLayout();

        CockpitView._PaneContainerFromSource(loose).Should().BeNull("nothing outside a tile panel is a pane to select");
    });
}

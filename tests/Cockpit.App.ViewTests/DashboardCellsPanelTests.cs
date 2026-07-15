using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Cockpit.App.Controls;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The dashboard's cells have to exist before the first widget is placed in one, or the widget spans the whole
/// dashboard — a Grid with no definitions is one big cell.
/// </summary>
/// <remarks>
/// This is a view test rather than a unit test because the bug it holds shut is a timing one, and timing needs a
/// layout pass to be real. The dashboard starts hidden behind whichever workspace is active, so the switch to it
/// is the first moment its panel exists; a view that builds the definitions itself has to find that panel, and
/// every event it could have waited for is too early. Measured: a control that starts hidden never raises Loaded
/// at all, and AttachedToVisualTree and TemplateApplied both fire while ItemsPanelRoot is still null.
/// </remarks>
[Collection("avalonia")]
public class DashboardCellsPanelTests
{
    private sealed class DashboardShape
    {
        public int Columns { get; init; }

        public int Rows { get; init; }
    }

    [Fact]
    public void APanelRealisedOnlyWhenTheDashboardIsSwitchedTo_StillHasItsCells() => HeadlessAvalonia.Run(() =>
    {
        var items = new ItemsControl
        {
            DataContext = new DashboardShape { Columns = 4, Rows = 3 },
            ItemsSource = new[] { "a widget" },
            ItemsPanel = new FuncTemplate<Panel?>(() => new DashboardCellsPanel
            {
                [!DashboardCellsPanel.ColumnsProperty] = new Binding(nameof(DashboardShape.Columns)),
                [!DashboardCellsPanel.RowsProperty] = new Binding(nameof(DashboardShape.Rows)),
            }),
        };

        // The dashboard's situation at startup: in the tree, but hidden behind the active Sessions workspace.
        var container = new Decorator { Child = items, IsVisible = false };
        var window = new Window { Content = container, Width = 400, Height = 300 };
        window.Show();

        items.ItemsPanelRoot.Should().BeNull("nothing realises a hidden panel — which is what made this bug");

        // The switch to the dashboard.
        container.IsVisible = true;
        window.UpdateLayout();

        var panel = items.ItemsPanelRoot.Should().BeOfType<DashboardCellsPanel>().Subject;
        panel.ColumnDefinitions.Should().HaveCount(4);
        panel.RowDefinitions.Should().HaveCount(3);
    });

    [Fact]
    public void APanelWhoseShapeChanges_RebuildsItsCells() => HeadlessAvalonia.Run(() =>
    {
        var panel = new DashboardCellsPanel { Columns = 2, Rows = 2 };

        panel.Columns = 5;

        panel.ColumnDefinitions.Should().HaveCount(5, "a dashboard resized in the ⚙ redraws at its new width");
        panel.RowDefinitions.Should().HaveCount(2);
    });

    [Fact]
    public void APanelWithNoDashboardActive_HasNoCells() => HeadlessAvalonia.Run(() =>
    {
        var panel = new DashboardCellsPanel { Columns = 0, Rows = 0 };

        panel.ColumnDefinitions.Should().BeEmpty();
        panel.RowDefinitions.Should().BeEmpty();
    });
}

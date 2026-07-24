using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using FluentAssertions;
using Xunit.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The project editor's fields scroll above a footer holding Cancel and Save. Its MCP checklist makes the content
/// taller than the dialog on any real machine, so the last row has to be able to reach a position above the
/// footer's top edge — Raymond saw it cut in half against that bar however far he scrolled.
/// <para>
/// Same failure as <see cref="PluginSettingsDialogScrollTests"/> proves generally: the bottom inset was set as
/// <c>ScrollViewer.Padding</c>, which the scroller leaves out of its extent, so those last pixels were unreachable.
/// This measures the real dialog rather than a stand-in, so the markup cannot drift back to the broken shape
/// without a test saying so.
/// </para>
/// </summary>
[Collection("avalonia")]
public class ProjectDialogScrollTests
{
    private readonly ITestOutputHelper _out;

    public ProjectDialogScrollTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void ScrolledToTheEnd_TheLastFieldClearsTheFooter() => HeadlessAvalonia.Run(() =>
    {
        var window = new ProjectDialog { DataContext = _ViewModelWithManyServers() };
        window.Show();
        window.UpdateLayout();

        var scroll = window.GetVisualDescendants().OfType<ScrollViewer>()
            .OrderByDescending(viewer => viewer.Bounds.Height).First();
        var footer = window.GetVisualDescendants().OfType<Button>()
            .First(button => (string?)button.Content == "Cancel");
        var lastRow = window.GetVisualDescendants().OfType<CheckBox>().Last();

        scroll.Offset = new Vector(0, scroll.Extent.Height);
        window.UpdateLayout();

        var rowBottom = lastRow.TranslatePoint(new Point(0, lastRow.Bounds.Height), window) ?? default;
        var footerTop = footer.TranslatePoint(new Point(0, 0), window) ?? default;
        var gap = footerTop.Y - rowBottom.Y;

        _out.WriteLine($"rowBottom={rowBottom.Y:0.#} footerTop={footerTop.Y:0.#} gap={gap:0.#} " +
                       $"viewport={scroll.Viewport.Height:0.#} extent={scroll.Extent.Height:0.#}");
        window.Close();

        scroll.Extent.Height.Should().BeGreaterThan(scroll.Viewport.Height, "the checklist has to overflow for this to mean anything");
        // Not merely non-negative: a row that ends exactly on the footer's edge still reads as pressed against it,
        // which is what Raymond saw next. The clearance is part of the fix, so it is part of the assertion.
        gap.Should().BeGreaterThanOrEqualTo(16, "the last row must end with air under it, not against the bar");
    });

    private static ProjectDialogViewModel _ViewModelWithManyServers()
    {
        var viewModel = new ProjectDialogViewModel();
        for (var index = 0; index < 24; index++)
        {
            viewModel.McpServers.Add(new McpServerSelectionItemViewModel($"cockpit-server-{index}"));
        }

        return viewModel;
    }
}

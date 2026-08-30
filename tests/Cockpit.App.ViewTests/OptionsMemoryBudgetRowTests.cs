using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1086: the shared memory budget's row in Options → Sessions. A setting the operator cannot reach, or one
/// whose spinner is not wired to the property that decides, is the same as not having the budget at all — and
/// neither shows up in a build. Measured here rather than looked at, since a screenshot proves it once.
/// </summary>
[Collection("avalonia")]
public class OptionsMemoryBudgetRowTests
{
    // The MaxWidth of the column the row sits in; past this the label, the spinner and its two captions wrap or clip.
    private const double ContentWidth = 900;

    [Fact]
    public void TheBudgetRow_FitsTheOptionsColumn() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();
        dialog.UpdateLayout();

        var row = _Row(dialog);

        // Measured at 518. The lower bound is the label's own 230 plus the spinner's 104: under that the row was
        // never laid out, and an upper bound on its own would pass on a row that is not there.
        Assert.InRange(row.DesiredSize.Width, 334, ContentWidth);

        dialog.Close();
    });

    [Fact]
    public void TheSpinner_ReadsAndWritesTheBudgetTheWarningDecidesOn() => HeadlessAvalonia.Run(() =>
    {
        var model = new CockpitViewModel();
        var dialog = new OptionsDialog { DataContext = model };
        dialog.Show();
        dialog.UpdateLayout();

        var spinner = _Row(dialog).GetVisualDescendants().OfType<NumericUpDown>().Single();

        Assert.Equal(MemoryPressure.DefaultBudgetPercent, spinner.Value);

        // A budget below the floor is refused by the spinner rather than clamped after the fact, so the number the
        // operator reads back is the number the warning uses.
        Assert.Equal(MemoryPressure.MinimumBudgetPercent, spinner.Minimum);
        Assert.Equal(100, spinner.Maximum);

        spinner.Value = 55;
        Assert.Equal(55, model.MemoryBudgetPercent);

        dialog.Close();
    });

    private static StackPanel _Row(OptionsDialog dialog) =>
        dialog.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "MemoryBudgetRow");
}

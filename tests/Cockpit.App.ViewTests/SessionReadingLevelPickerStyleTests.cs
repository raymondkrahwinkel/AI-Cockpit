using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.UsagePill;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The reading-level picker sits in the header's own row of pills and has to read as one of them rather than as a
/// form control parked beside them (Raymond, live test 2026-07-31). Measured here against the usage pill actually
/// rendered next to it, not against copied numbers, so the two cannot drift apart silently.
/// </summary>
[Collection("avalonia")]
public class SessionReadingLevelPickerStyleTests
{
    [Fact]
    public void ItMatchesTheUsagePillItSitsBeside() => HeadlessAvalonia.Run(() =>
    {
        var vm = new SessionViewModel { ContextUsedPercent = 11, UsagePillVisibleFields = [UsagePillField.Context] };
        var window = new Window { Width = 900, Height = 700, Content = new SessionView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var picker = window.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "ReadingLevelPicker");
        // The pill is the shortest rendered border carrying the pill radius — the segmented usage pill itself.
        var pill = window.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Bounds.Height > 0 && b.CornerRadius.TopLeft >= 20)
            .OrderBy(b => b.Bounds.Height).First();

        var pickerHeight = picker.Bounds.Height;
        var pickerRadius = picker.CornerRadius.TopLeft;
        var pillHeight = pill.Bounds.Height;
        var pillRadius = pill.CornerRadius.TopLeft;

        window.Close();

        // Before the pill class: 28px tall at radius 9, against the pill's 19 at radius 20.
        Assert.Equal(pillHeight, pickerHeight);
        Assert.Equal(pillRadius, pickerRadius);
    });
}

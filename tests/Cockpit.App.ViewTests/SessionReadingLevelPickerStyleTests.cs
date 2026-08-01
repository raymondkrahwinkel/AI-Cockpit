using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Sessions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-562: the reading level moved off the header strip into the sliders flyout, beside the session's own live
/// controls — it was the last control still standing loose on the bar. The trap this keeps shut is the button's
/// former <c>IsVisible="{Binding HasLiveControls}"</c>: on a provider that declares no live controls, that would
/// have taken the reading level off screen with the button, leaving nothing visibly broken and no way to reach
/// the setting. (This class previously held AC-138's pill-shape check, which the move retires: the picker is a
/// flyout row now, not one of the header's pills.)
/// </summary>
[Collection("avalonia")]
public class SessionReadingLevelPickerStyleTests
{
    [Fact]
    public void TheReadingLevelIsNoLongerOnTheHeaderStrip() => HeadlessAvalonia.Run(() =>
    {
        var (window, _) = _Session(withLiveControls: true);
        var onTheBar = window.GetVisualDescendants().OfType<ComboBox>().Any(c => c.Name == "ReadingLevelPicker");
        window.Close();

        Assert.False(onTheBar);
    });

    /// <summary>
    /// Criterion 3, and the reason this ticket is not a one-line move: with no live controls the flyout still
    /// holds the reading level, so the button that opens it has to be there too.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSlidersButtonAndItsReadingLevelSurviveAProviderWithoutLiveControls(bool withLiveControls) =>
        HeadlessAvalonia.Run(() =>
        {
            var (window, viewModel) = _Session(withLiveControls);
            var button = window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "SessionSettingsButton");
            var visible = button.IsVisible;

            var flyout = (Flyout)button.Flyout!;
            flyout.ShowAt(button);
            Dispatcher.UIThread.RunJobs();

            var picker = ((Control)flyout.Content!).GetLogicalDescendants().OfType<ComboBox>()
                .Single(c => c.Name == "ReadingLevelPicker");

            // Criterion 2: still two-way onto the running session, so switching in here switches it live.
            picker.SelectedValue = ReadingLevel.Simple;
            Dispatcher.UIThread.RunJobs();
            var applied = viewModel.ReadingLevel;

            flyout.Hide();
            window.Close();

            Assert.True(visible);
            Assert.Equal(ReadingLevel.Simple, applied);
        });

    /// <summary>Criterion 4: a TTY pane has no reading level — not on its bar, and no flyout to hide one in.</summary>
    [Fact]
    public void ATtyPaneCarriesNoReadingLevelAnywhere() => HeadlessAvalonia.Run(() =>
    {
        var window = new Window { Width = 900, Height = 700, Content = new TtyView { DataContext = new TtyViewModel() } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var names = window.GetVisualDescendants().OfType<Control>().Select(c => c.Name).ToList();
        window.Close();

        Assert.DoesNotContain("ReadingLevelPicker", names);
        Assert.DoesNotContain("SessionSettingsButton", names);
    });

    private static (Window Window, SessionViewModel ViewModel) _Session(bool withLiveControls)
    {
        // The design-time constructor seeds two sample live controls, so "without" is that seed removed —
        // which is exactly the shape a provider declaring none produces.
        var viewModel = new SessionViewModel();
        if (!withLiveControls)
        {
            viewModel.LiveControls.Clear();
        }

        var window = new Window { Width = 900, Height = 700, Content = new SessionView { DataContext = viewModel } };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        return (window, viewModel);
    }
}

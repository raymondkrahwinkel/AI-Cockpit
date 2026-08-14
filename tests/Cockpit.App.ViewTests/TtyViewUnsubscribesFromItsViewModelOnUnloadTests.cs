using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-758: a closed pane's <c>TtyView</c> stayed subscribed to its view model's <c>LaunchRequested</c>,
/// <c>VoiceTranscriptReady</c> and <c>PropertyChanged</c> forever — <c>OnDataContextChanged</c>'s matching
/// unsubscribes never run on a normal close, only <c>OnUnloaded</c> does. Rooting the view model behind the
/// (still-subscribed) view kept its whole visual tree alive for the life of the app.
/// </summary>
[Collection("avalonia")]
public class TtyViewUnsubscribesFromItsViewModelOnUnloadTests
{
    [Fact]
    public void Unloaded_DropsTheViewModelsLaunchRequestedAndVoiceTranscriptReadySubscriptions()
    {
        HeadlessAvalonia.Run(() =>
        {
            var view = new TtyView();
            var window = new Window { Content = view };
            window.Show();
            var viewModel = new TtyViewModel();
            view.DataContext = viewModel; // OnDataContextChanged subscribes.

            Assert.NotNull(_InvocationTarget(viewModel, "LaunchRequested"));
            Assert.NotNull(_InvocationTarget(viewModel, "VoiceTranscriptReady"));

            typeof(TtyView)
                .GetMethod("OnUnloaded", BindingFlags.NonPublic | BindingFlags.Instance, [typeof(object), typeof(RoutedEventArgs)])!
                .Invoke(view, [null, new RoutedEventArgs()]);

            Assert.Null(_InvocationTarget(viewModel, "LaunchRequested"));
            Assert.Null(_InvocationTarget(viewModel, "VoiceTranscriptReady"));
        });
    }

    private static Delegate? _InvocationTarget(TtyViewModel viewModel, string eventFieldName) =>
        (Delegate?)typeof(TtyViewModel).GetField(eventFieldName, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(viewModel);
}

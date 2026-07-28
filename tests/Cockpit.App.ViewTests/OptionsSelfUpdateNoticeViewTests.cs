using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Updates;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// A copy the updater did not install cannot fetch a newer build over itself (AC-385), and the Updates tab says so
/// rather than leaving it to be discovered. Rendered rather than reasoned about: the notice hangs on a negated
/// binding, and a negation is exactly the kind of thing that reads correctly and does the opposite.
/// </summary>
[Collection("avalonia")]
public class OptionsSelfUpdateNoticeViewTests
{
    [Fact]
    public void WhenThisCopyCannotUpdateItself_TheNoticeShows_AndTheReleasePageIsStillOffered() => HeadlessAvalonia.Run(() =>
    {
        // The parameterless view model has no probe, which is the same answer a tarball, a checkout and this test
        // host all get: not packaged.
        var dialog = _OpenUpdatesTab(new CockpitViewModel());

        Assert.True(_Notice(dialog).IsVisible);

        // The other half of the sentence: told what it cannot do, and given the place that can.
        Assert.Contains(dialog.GetVisualDescendants().OfType<Button>(),
            button => button.Content as string == "Open the release");

        dialog.Close();
    });

    [Fact]
    public void WhenThisCopyCanUpdateItself_TheNoticeIsGone() => HeadlessAvalonia.Run(() =>
    {
        var probe = Substitute.For<IUpdateSupportProbe>();
        probe.Detect().Returns(UpdateSupport.Supported);

        var dialog = _OpenUpdatesTab(_ViewModelWith(probe));

        Assert.False(_Notice(dialog).IsVisible);

        dialog.Close();
    });

    private static TextBlock _Notice(OptionsDialog dialog) =>
        dialog.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "SelfUpdateNotice");

    /// <summary>
    /// A tab's content is only realised once it is the selected tab, so select Updates and force a layout pass
    /// before reaching into it.
    /// </summary>
    private static OptionsDialog _OpenUpdatesTab(CockpitViewModel cockpit)
    {
        var dialog = new OptionsDialog { DataContext = cockpit };
        dialog.Show();

        var tabs = dialog.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Header as string == "Updates");
        dialog.UpdateLayout();

        return dialog;
    }

    /// <summary>
    /// The real constructor rather than the design-time one, because the probe only reaches the view model through
    /// that path — and whether it arrives at all is half of what this is checking.
    /// </summary>
    private static CockpitViewModel _ViewModelWith(IUpdateSupportProbe probe)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            updateSupportProbe: probe);
    }
}

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
/// The Updates tab's "Update now"/"Install on next start" pair (AC-388), and the plain "Open the release" link they
/// replace for a copy the updater did not install. Rendered rather than reasoned about (AC-379): each assertion is
/// against the actual <see cref="Button.IsVisible"/>, not <see cref="CockpitViewModel.CanUpdateItself"/> or
/// <see cref="CockpitViewModel.HasUpdate"/> on their own — a button hung off a container's own condition, or an
/// internal flag a test cannot see, is exactly the shape that let an offer sit behind an invisible control before.
/// </summary>
[Collection("avalonia")]
public class OptionsSelfUpdateButtonsViewTests
{
    [Fact]
    public Task WhenThisCopyCanUpdateItself_AndABuildIsOffered_UpdateNowAndInstallOnNextStartShow_AndOpenReleaseDoesNot() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var probe = Substitute.For<IUpdateSupportProbe>();
            probe.Detect().Returns(UpdateSupport.Supported);
            var updates = Substitute.For<IUpdateService>();
            updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
                .Returns(new UpdateCheckResult(new AppRelease("1.2.3", "notes", "https://example.test/1.2.3"), null));

            var cockpit = _ViewModelWith(probe, updates);
            await cockpit.CheckForUpdatesAsync();

            var dialog = _OpenUpdatesTab(cockpit);

            Assert.True(_Button(dialog, "UpdateNowButton").IsVisible);
            Assert.True(_Button(dialog, "InstallOnNextStartButton").IsVisible);
            Assert.False(_Button(dialog, "OpenReleaseButton").IsVisible);

            dialog.Close();
        });

    [Fact]
    public Task WhenThisCopyCannotUpdateItself_AndABuildIsOffered_OpenReleaseShows_AndTheSelfUpdateButtonsDoNot() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            // No probe supplied at all reads the same as one that answers NotPackaged (the constructor's default).
            var updates = Substitute.For<IUpdateService>();
            updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
                .Returns(new UpdateCheckResult(new AppRelease("1.2.3", "notes", "https://example.test/1.2.3"), null));

            var cockpit = _ViewModelWith(probe: null, updates);
            await cockpit.CheckForUpdatesAsync();

            var dialog = _OpenUpdatesTab(cockpit);

            Assert.False(_Button(dialog, "UpdateNowButton").IsVisible);
            Assert.False(_Button(dialog, "InstallOnNextStartButton").IsVisible);
            Assert.True(_Button(dialog, "OpenReleaseButton").IsVisible);

            dialog.Close();
        });

    [Fact]
    public void WhenNoBuildIsOffered_NoneOfTheThreeButtonsShow() => HeadlessAvalonia.Run(() =>
    {
        var probe = Substitute.For<IUpdateSupportProbe>();
        probe.Detect().Returns(UpdateSupport.Supported);

        var dialog = _OpenUpdatesTab(_ViewModelWith(probe, updates: null));

        Assert.False(_Button(dialog, "UpdateNowButton").IsVisible);
        Assert.False(_Button(dialog, "InstallOnNextStartButton").IsVisible);
        Assert.False(_Button(dialog, "OpenReleaseButton").IsVisible);

        dialog.Close();
    });

    // AC-1515: the changelog is read when the build is found; a read that failed is retried on the click, and a good one is not.
    [Fact]
    public Task WhatsNew_OpensOnTheChangesReadAhead_AndReadsAgainOnlyAfterAFailedReadAhead() =>
        HeadlessAvalonia.RunAsync(async () =>
        {
            var updates = Substitute.For<IUpdateService>();
            updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
                .Returns(new UpdateCheckResult(new AppRelease("1.2.3", "notes", "https://example.test/1.2.3"), null));
            updates.ReadChangesAsync("1.2.3", Arg.Any<CancellationToken>())
                .Returns(ChangelogResult.Failed("offline"), new ChangelogResult("- fixed: a thing", null));
            var shown = new List<Task<ChangelogResult>>();
            var dialogs = Substitute.For<ISessionDialogService>();
            dialogs.ShowWhatsNewDialogAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Do<Task<ChangelogResult>>(shown.Add)).Returns(Task.CompletedTask);

            var cockpit = _ViewModelWith(probe: null, updates, dialogs);
            await cockpit.CheckForUpdatesAsync();
            await updates.Received(1).ReadChangesAsync("1.2.3", Arg.Any<CancellationToken>());

            await cockpit.ShowWhatsNewCommand.ExecuteAsync(null);
            await cockpit.ShowWhatsNewCommand.ExecuteAsync(null);

            await updates.Received(2).ReadChangesAsync("1.2.3", Arg.Any<CancellationToken>());
            Assert.Equal("- fixed: a thing", (await shown[0]).Markdown);
            Assert.Same(shown[0], shown[1]);
        });

    private static Button _Button(OptionsDialog dialog, string name) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == name);

    /// <summary>A tab's content is only realised once it is the selected tab, so select Updates and force a layout pass before reaching into it.</summary>
    private static OptionsDialog _OpenUpdatesTab(CockpitViewModel cockpit)
    {
        var dialog = new OptionsDialog { DataContext = cockpit };
        dialog.Show();

        // AC-1000: the sidebar's CategoryNav ListBox replaced the old per-tab TabControl.
        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "updates");
        dialog.UpdateLayout();

        return dialog;
    }

    /// <summary>The real constructor rather than the design-time one, because the probe/update service only reach the view model through that path.</summary>
    private static CockpitViewModel _ViewModelWith(IUpdateSupportProbe? probe, IUpdateService? updates, ISessionDialogService? dialogs = null)
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
            dialogs ?? Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            updateService: updates,
            updateSupportProbe: probe);
    }
}

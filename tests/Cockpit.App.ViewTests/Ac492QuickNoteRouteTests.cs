using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Hotkeys;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.QuickNotes;
using Cockpit.Core.Abstractions.Screenshots;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-492: the public way in without a hotkey — the command palette, reached from the whole `CockpitView` by its
// gesture — opens the quick-note window, and asking a second time brings that one back instead of a second one.
[Collection("avalonia")]
public sealed class Ac492QuickNoteRouteTests
{
    [Fact]
    public void ThePaletteCommand_ReachedFromTheCockpitView_OpensExactlyOneQuickNoteWindow() => HeadlessAvalonia.Run(() =>
    {
        IReadOnlyList<PaletteCommand> palette = [];
        var dialogs = Substitute.For<ISessionDialogService>();
        dialogs.ShowCommandPaletteDialogAsync(Arg.Do<IReadOnlyList<PaletteCommand>>(commands => palette = commands)).Returns(Task.CompletedTask);
        var cockpit = _Cockpit(dialogs);
        var quickNotes = new QuickNoteCoordinator(_Hotkeys(), cockpit, Substitute.For<IProjectMemoryNoteWriter>());
        cockpit.QuickNotes = quickNotes;

        var window = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
        window.Show();
        window.UpdateLayout();
        try
        {
            window.KeyPressQwerty(PhysicalKey.K, RawInputModifiers.Control);
            var command = Assert.Single(palette, entry => entry.Title == "Quick note");

            command.Invoke();
            var opened = quickNotes.Window;
            Assert.NotNull(opened);
            Assert.True(opened.IsVisible);

            command.Invoke();
            Assert.Same(opened, quickNotes.Window);
        }
        finally
        {
            quickNotes.Window?.Close();
            window.Close();
        }
    });

    // The coordinator needs a hotkey coordinator to subscribe to; this one is never armed, so every store is a blank.
    private static GlobalHotkeyCoordinator _Hotkeys() => new(
        Substitute.For<IGlobalHotkeyService>(),
        Substitute.For<IVoiceSettingsStore>(),
        Substitute.For<IScreenshotSettingsStore>(),
        Substitute.For<IAssistantSettingsStore>(),
        Substitute.For<IQuickNoteSettingsStore>(),
        Substitute.For<IHotkeyExclusivityGuard>(),
        Substitute.For<IToastService>(),
        NullLogger<GlobalHotkeyCoordinator>.Instance);

    private static CockpitViewModel _Cockpit(ISessionDialogService dialogs)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(new LayoutSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogs,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal);
    }
}

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Layout;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.Voice;
using Cockpit.Plugins.Abstractions.Docking;
using Material.Icons;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-1710: the host contract a plugin's dock panel gets — `KeepAlive` keeps one view instance, `CanFloat` moves that
// same instance into its own window and back — driven through the real rail, since the rail is what used to rebuild it.
[Collection("avalonia")]
public sealed class DockPanelKeepAliveFloatTests
{
    private const string KeptId = "sample.kept";

    // L2a (UI behaviour): criteria 1 and 2. The floating row also carries L2e (state): the stand survives a restart.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AKeptPanel_KeepsOneViewInstance_InTheRailAndInItsOwnWindow(bool floating)
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            LayoutSettings? saved = null;
            var layout = _LayoutStore(new LayoutSettings { AssistantDocked = true });
            layout.When(store => store.SaveAsync(Arg.Any<LayoutSettings>())).Do(call => saved = call.Arg<LayoutSettings>());
            var cockpit = _Cockpit(layout, _Panels());

            var main = new Window { Width = 1100, Height = 760, Content = new CockpitView { DataContext = cockpit } };
            main.Show();

            try
            {
                await cockpit.ToggleDockPanelCommand.ExecuteAsync(KeptId);
                Dispatcher.UIThread.RunJobs();
                var view = _RailContent(main);
                Assert.NotNull(view);

                if (!floating)
                {
                    await cockpit.ToggleDockPanelCommand.ExecuteAsync(KeptId);
                    await cockpit.ToggleDockPanelCommand.ExecuteAsync("sample.other");
                    await cockpit.ToggleDockPanelCommand.ExecuteAsync(KeptId);
                    Dispatcher.UIThread.RunJobs();

                    Assert.Same(view, _RailContent(main));
                    return;
                }

                await cockpit.UndockOpenDockPanelCommand.ExecuteAsync(null);
                Dispatcher.UIThread.RunJobs();

                var window = cockpit.DockHost.WindowFor(KeptId);
                Assert.NotNull(window);
                Assert.Contains(view, window.GetVisualDescendants());
                Assert.DoesNotContain(cockpit.DockPanels, panel => panel.Id == KeptId);

                // A restart reads the stand back off what was saved: floating, so a window and no tab.
                Assert.NotNull(saved);
                var restarted = _Cockpit(_LayoutStore(saved), _Panels());
                Assert.True(restarted.IsDockPanelFloating(KeptId));

                // The load replaces the default stand, where the assistant floats: docked in the file, docked after it.
                Assert.True(cockpit.AssistantDocked);
                Assert.True(restarted.AssistantDocked);
                restarted.DockHost.RestoreFloatingWindows();
                var reopened = restarted.DockHost.WindowFor(KeptId);
                Assert.NotNull(reopened);
                reopened.Close();

                await cockpit.DockHost.ShowInAsync(KeptId, floating: false);
                Dispatcher.UIThread.RunJobs();

                Assert.Null(cockpit.DockHost.WindowFor(KeptId));
                Assert.Same(view, _RailContent(main));
            }
            finally
            {
                main.Close();
            }
        });
    }

    private static DockPanelRegistry _Panels()
    {
        var panels = new DockPanelRegistry();
        panels.Register(new DockPanelRegistration("sample.other", "Other", MaterialIconKind.ViewDashboardOutline, () => new TextBlock()));
        panels.Register(new DockPanelRegistration(KeptId, "Kept", MaterialIconKind.Web, () => new TextBlock { Text = "kept" })
        {
            KeepAlive = true,
            CanFloat = true,
        });
        return panels;
    }

    private static object? _RailContent(Window main) =>
        main.GetVisualDescendants().OfType<ContentControl>().First(control => control.Name == "DockPanelContent").Content;

    private static ILayoutSettingsStore _LayoutStore(LayoutSettings settings)
    {
        var layout = Substitute.For<ILayoutSettingsStore>();
        layout.LoadAsync().Returns(settings);
        return layout;
    }

    private static CockpitViewModel _Cockpit(ILayoutSettingsStore layout, IDockPanelRegistry panels)
    {
        var notifications = Substitute.For<INotificationSettingsStore>();
        notifications.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplay = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplay.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehavior = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehavior.LoadAsync().Returns(new SessionBehaviorSettings());
        var voice = Substitute.For<IVoiceSettingsStore>();
        voice.LoadAsync().Returns(new VoiceSettings());
        var terminal = Substitute.For<ITerminalSettingsStore>();
        terminal.LoadAsync().Returns(new TerminalSettings());

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            Substitute.For<ISessionDialogService>(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notifications,
            transcriptDisplay,
            sessionBehavior,
            layout,
            voice,
            terminal,
            dockPanelRegistry: panels);
    }
}

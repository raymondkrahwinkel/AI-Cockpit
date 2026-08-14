using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Services;
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
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-772: the toolbar moved from the session grid's own header to the workspace tab strip, so its actions are
/// reachable from every workspace type — not only from a Sessions workspace that already has a session in it, which
/// is what left Depot's servers unreachable from the page the operator lands on. Measured against the real markup:
/// where the host sits in the tree is exactly what this ticket changed, so a view model assertion would prove nothing.
/// </summary>
[Collection("avalonia")]
public class GlobalToolbarTests
{
    private static ToolbarAction _Action(string title, Func<Task>? onInvoke = null) =>
        new(title, null, onInvoke ?? (() => Task.CompletedTask));

    [Fact]
    public async Task TheToolbar_RendersItsActions_OnAProjectsWorkspace() =>
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var cockpit = new CockpitViewModel();
            ((IPluginContributionSink)cockpit).AddToolbarAction("depot", _Action("Depot servers"));

            // The workspace type with no session in it at all — the case the old placement could not draw.
            await cockpit.Workspaces.OpenWorkspaceAsync(WorkspaceType.Projects.Id);

            var view = new CockpitView { DataContext = cockpit };
            var window = new Window { Content = view, Width = 1100, Height = 800 };
            window.Show();
            window.UpdateLayout();

            var label = view.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(text => text.Text == "Depot servers");

            Assert.NotNull(label);

            window.Close();
        });

    [Fact]
    public void AFreshInstall_WithNoActionsRegistered_DrawsNoToolbarAtAll() => HeadlessAvalonia.Run(() =>
    {
        var cockpit = new CockpitViewModel();

        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        window.UpdateLayout();

        // The host is in the tree either way — what matters is that it contributes nothing, so the strip loses no
        // height on a cockpit without plugins.
        var host = view.GetVisualDescendants().OfType<PluginToolbarHost>().FirstOrDefault();

        Assert.NotNull(host);
        Assert.Empty(host.Children);
        Assert.Equal(0, host.Bounds.Height);

        window.Close();
    });

    [Fact]
    public void AnActionThatThrows_LeavesTheCockpitUp_AndLandsInPluginDiagnostics() => HeadlessAvalonia.Run(() =>
    {
        var diagnostics = new PluginDiagnostics();
        var cockpit = _NewCockpit(diagnostics);
        ((IPluginContributionSink)cockpit).AddToolbarAction(
            "depot", _Action("Depot servers", () => throw new InvalidOperationException("no connection")));

        var view = new CockpitView { DataContext = cockpit };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        window.Show();
        window.UpdateLayout();

        var button = view.GetVisualDescendants().OfType<Button>()
            .First(candidate => AutomationProperties.GetName(candidate) == "Depot servers");

        // The host builds buttons with a Click handler rather than a Command, so drive it the way the operator does.
        // Would take the app down if the host did not catch it — that this returns at all is half the assertion.
        var exception = Record.Exception(() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        Assert.Null(exception);
        var failure = Assert.Single(diagnostics.Failures);
        Assert.Equal("depot", failure.FolderId);
        Assert.Equal("toolbar-action", failure.Phase);
        Assert.Contains("no connection", failure.Error);

        window.Close();
    });

    // The design-time constructor carries no PluginDiagnostics, and this test is about what lands in it — so the real
    // constructor it is, with everything it does not care about substituted. Same shape as
    // ProjectsWorkspaceSharedProjectsTests' own helper.
    private static CockpitViewModel _NewCockpit(PluginDiagnostics diagnostics)
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
            pluginDiagnostics: diagnostics);
    }
}

extern alias schedulesUi;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using Cockpit.Plugin.Schedules;
using Cockpit.Plugin.Schedules.Contracts;
using Cockpit.Plugin.Schedules.Engine;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.UI;
using UiParts = schedulesUi::Cockpit.Plugin.Schedules.UI;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1546, layer 2 view journey: the Schedules screen on the real backend part, over a loopback channel, in the app's theme. Set
/// SCHEDULES_RENDER_DIR to also write the four scenes in Dark and Light, for the comparison with the mockup.
/// </summary>
[Collection("avalonia")]
public class SchedulesScreenJourneyTests
{
    // Criteria 1, 3, 4, 5 and 6 in the order an operator meets them: the menu button and shortcut open the screen, Run now
    // shows Running… and then the outcome without a reload, the builder writes and reads back cron and refuses an invalid
    // one, delivery lists exactly the open channels, and switching on a Write task asks about Run unattended.
    [Fact]
    public Task TheOperatorOpensSchedules_RunsATask_EditsOne_AndSwitchesOnAWriteTask() => HeadlessAvalonia.RunAsync(async () =>
    {
        var storage = new _Storage();
        var channel = new _Loopback();
        var backend = Substitute.For<ICockpitHost>();
        backend.Storage.Returns(storage);
        backend.Cache.Returns(storage);
        backend.Channel.Returns(channel);
        backend.OperatorMessageChannels.Returns([new OperatorMessageChannelInfo("discord", "Discord", [new OperatorMessageTarget("dm", "Direct message to you")])]);
        using var held = new ManualResetEventSlim();
        backend.When(host => host.ShowToast(Arg.Any<string>(), Arg.Any<PluginToastSeverity>(), Arg.Any<string?>(), Arg.Any<Action?>()))
            .Do(_ => held.Wait(TimeSpan.FromSeconds(10)));
        new TaskStore(storage).Save(_Seed());
        using var plugin = new SchedulesPlugin();
        plugin.Initialize(backend);

        var ui = Substitute.For<ICockpitUiHost>();
        ui.Channel.Returns(channel);
        ui.GetProfilesAsync().Returns([new PluginProfileInfo("Zyra", "ClaudeCli", string.Empty)]);
        Action? menu = null;
        Func<Control>? opened = null;
        ui.AddSideMenuButtonWithBadge("Schedules", Arg.Do<Action>(invoke => menu = invoke)).Returns(new SideMenuButtonBadge());
        ui.ShowDialogAsync("Schedules", Arg.Do<Func<Control>>(factory => opened = factory), "schedules", Arg.Any<double>(), Arg.Any<double>())
            .Returns(new TaskCompletionSource().Task);

        new UiParts.SchedulesUi().InitializeUi(ui);
        ui.Received(1).AddShortcut(Arg.Is<PluginShortcut>(shortcut => shortcut.Id == UiParts.SchedulesUi.ShortcutId && shortcut.DefaultGesture == "Ctrl+Shift+S"));
        (menu ?? throw new InvalidOperationException("No Schedules button under Plugins."))();
        await _Until(() => opened is not null, "the screen opens from its menu button");
        var screen = (UiParts.SchedulesScreen)(opened ?? throw new InvalidOperationException("The screen did not open."))();
        using var scene = RenderedScene.Show(screen, 1280, 820);

        var standUp = screen.State.Tasks.Single(view => view.Task.Name == "Stand-up").Task;
        await screen.Tasks.RunNowAsync(standUp);
        await _Until(() => _Texts(screen).Contains("Running…"), "Run now shows Running…");
        held.Set();
        await _Until(() => _Texts(screen).Contains("Succeeded") && !_Texts(screen).Contains("Running…"), "the outcome replaces Running… without a reload");
        screen.Tasks.Select("news");
        _Render(scene.Window, "schedules-tasks");

        screen.Edit(null);
        var editor = screen.Editor ?? throw new InvalidOperationException("No editor.");
        editor.SetWeekly("07:30", "1", "2", "3", "4", "5");
        await editor.PreviewSettledAsync();
        Assert.Equal("30 7 * * 1-5", editor.Schedule);
        Assert.StartsWith("Every weekday at 07:30", editor.PreviewText, StringComparison.Ordinal);
        Assert.Equal(["Cockpit notification", "Discord"], editor.DeliveryChoices);
        _Render(scene.Window, "schedules-editor");
        editor.SetCron("61 7 * * *");
        await editor.PreviewSettledAsync();
        Assert.Equal((UiParts.TaskEditorView.InvalidSchedule, false), (editor.PreviewText, editor.CanSave));

        screen.Show(UiParts.SchedulesScreen.TasksTab);
        var backup = screen.State.Tasks.Single(view => view.Task.Name == "Weekly backup check").Task;
        await screen.Tasks.ToggleAsync(backup, null);
        Assert.Contains("Run unattended", _Texts(screen.Dialog));
        _Click(screen.Dialog, "Cancel");
        await screen.State.RefreshAsync();
        Assert.False(screen.Dialog.IsVisible);
        Assert.False(new TaskStore(storage).Load()?.Single(task => task.Name == "Weekly backup check").IsEnabled);

        screen.Show(UiParts.SchedulesScreen.RunsTab);
        screen.Runs.Select(screen.State.Runs[0].Id);
        _Render(scene.Window, "schedules-runs");
        screen.Show(UiParts.SchedulesScreen.SettingsTab);
        _Render(scene.Window, "schedules-settings");
        Assert.Contains("Every Cockpit session: the assistant and all agents", _Texts(screen));
    });

    private static List<ScheduledTask> _Seed() =>
    [
        new()
        {
            Id = "news", Name = "Morning news", Schedule = "30 7 * * 1-5", TimeZone = "Europe/Amsterdam",
            Action = new TaskAction(TaskActionKind.AgentPrompt, "Summarise the news.") { Profile = "Zyra" },
            Delivery = [new DeliveryTarget("discord", "dm")],
        },
        new()
        {
            Id = "backup", Name = "Weekly backup check", Schedule = "0 9 * * 1", TimeZone = "Europe/Amsterdam", IsEnabled = false,
            Action = new TaskAction(TaskActionKind.AgentPrompt, "Check last night's backup.") { Profile = "Zyra", WorkingDirectory = "/srv/backup", Permission = AgentPermission.Write },
            Delivery = [new DeliveryTarget(DeliveryTarget.CockpitNotification)],
        },
        new()
        {
            Id = "stand-up", Name = "Stand-up", Schedule = "0 9 * * *", TimeZone = "Europe/Amsterdam",
            Action = new TaskAction(TaskActionKind.Notification, "Stand-up in five minutes."),
            Delivery = [new DeliveryTarget(DeliveryTarget.CockpitNotification)],
        },
    ];

    private static List<string> _Texts(Visual root)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. root.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];
    }

    private static void _Click(Visual root, string label) =>
        root.GetVisualDescendants().OfType<Button>().First(button => button.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task _Until(Func<bool> condition, string what)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Never happened: {what}.");
    }

    // The renders for the PR, next to the mockup: each scene in Dark and Light, only when a folder is named.
    private static void _Render(Window window, string scene)
    {
        if (Environment.GetEnvironmentVariable("SCHEDULES_RENDER_DIR") is not { Length: > 0 } folder || Application.Current is not { } app)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            app.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame to save.");
            frame.Save(Path.Combine(folder, $"{scene}-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        app.RequestedThemeVariant = ThemeVariant.Default;
    }

    // Settings and cache in one dictionary, as a plugin host gives them.
    private sealed class _Storage : IPluginStorage, IPluginCache
    {
        private readonly Dictionary<string, string> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) ? JsonSerializer.Deserialize<T>(value) : default;

        public void Set<T>(string key, T value) => _values[key] = JsonSerializer.Serialize(value);

        public void Remove(string key) => _values.Remove(key);
    }

    // Both halves of the plugin's channel in one process: the UI's calls reach the backend's handlers, its events the UI.
    private sealed class _Loopback : IPluginBackendChannel, IPluginUiChannel
    {
        private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = new(StringComparer.Ordinal);
        private readonly List<(string Name, Action<PluginChannelEvent> Handler)> _subscribers = [];
        private long _sequence;

        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
        {
            _handlers[action] = handler;
            return new _Off(() => _handlers.Remove(action));
        }

        public void Publish(string name, JsonElement payload)
        {
            List<Action<PluginChannelEvent>> handlers;
            lock (_subscribers)
            {
                handlers = [.. _subscribers.Where(subscriber => subscriber.Name == name).Select(subscriber => subscriber.Handler)];
            }

            var seq = Interlocked.Increment(ref _sequence);
            handlers.ForEach(handler => handler(new PluginChannelEvent(name, seq, payload)));
        }

        // Off the UI thread, as the host's relay is: a handler that waits must not hold up the screen that asked.
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.Run(() => _handlers[action](payload, cancellationToken), cancellationToken);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler)
        {
            var entry = (name, handler);
            lock (_subscribers)
            {
                _subscribers.Add(entry);
            }

            return new _Off(() =>
            {
                lock (_subscribers)
                {
                    _subscribers.Remove(entry);
                }
            });
        }

        private sealed class _Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }
    }
}

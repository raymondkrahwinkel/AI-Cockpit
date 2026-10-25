extern alias productivityUi;

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Plugin.Productivity;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;
using UiParts = productivityUi::Cockpit.Plugin.Productivity.UI;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1722 layer 2 view journey: one real Productivity UI and backend channel over host-owned time entries;
/// AC-1731 adds the measured time from the session timeline beside them.
/// </summary>
[Collection("avalonia")]
public sealed class ProductivityHoursJourneyTests
{
    [Fact]
    public Task OperatorRecordsTimeUsesStopwatchAndNavigatesDays() => HeadlessAvalonia.RunAsync(async () =>
    {
        var timeEntries = new _TimeEntries();
        var channel = new _Loopback();
        var backend = Substitute.For<ICockpitHost>();
        backend.Channel.Returns(channel);
        backend.TimeEntries.Returns(timeEntries);
        backend.Timeline.Returns(new _Timeline());
        using var plugin = new ProductivityPlugin();
        plugin.Initialize(backend);

        var ui = Substitute.For<ICockpitUiHost>();
        ui.Channel.Returns(channel);
        ui.ActiveSessionWorkingDirectory.Returns(Path.Combine("work", "cockpit"));
        ui.OpenWorkspaceAsync(UiParts.ProductivityUi.WorkspaceTypeId).Returns(Task.CompletedTask);
        ui.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var badge = new SideMenuButtonBadge();
        Action? open = null;
        Func<Control>? sidebarFactory = null;
        WorkspaceTypeRegistration? workspace = null;
        ui.AddSideMenuButtonWithBadge("Uren", Arg.Do<Action>(value => open = value), MaterialIconKind.ClockOutline).Returns(badge);
        ui.When(host => host.AddSideMenuSection("Tijd", Arg.Any<Func<Control>>()))
            .Do(call => sidebarFactory = call.Arg<Func<Control>>());
        ui.When(host => host.AddWorkspaceType(Arg.Any<WorkspaceTypeRegistration>()))
            .Do(call => workspace = call.Arg<WorkspaceTypeRegistration>());

        new UiParts.ProductivityUi().InitializeUi(ui);
        (open ?? throw new InvalidOperationException("No Uren button."))();
        await ui.Received(1).OpenWorkspaceAsync(UiParts.ProductivityUi.WorkspaceTypeId);
        var registration = workspace ?? throw new InvalidOperationException("No Uren workspace type.");
        var screen = registration.CreateBody(Substitute.For<IWorkspaceContext>());
        using var scene = RenderedScene.Show(screen, 1280, 820);
        await _Until(() => badge.Text == "0u10 vandaag", "the initial badge with ten measured minutes");
        Assert.Equal(SideMenuButtonBadgeTone.Success, badge.Tone);
        var idleSidebar = (sidebarFactory ?? throw new InvalidOperationException("No Tijd sidebar."))();
        using (var idleScene = RenderedScene.Show(idleSidebar, 360, 180))
        {
            Assert.Contains("Geen stopwatch actief", _Texts(idleSidebar));
            _Control<Button>(idleSidebar, "SidebarStart");
        }

        _Control<TextBox>(screen, "Description").Text = "Telefonisch overleg met klant XXX";
        _Control<TextBox>(screen, "Duration").Text = "ongeldig";
        _Click(screen, "AddEntry");
        await _Until(() => _Texts(screen).Any(text => text.Contains("geldige duur", StringComparison.OrdinalIgnoreCase)), "invalid duration feedback");
        Assert.Empty(timeEntries.Entries);

        _Control<TextBox>(screen, "Duration").Text = "0:15";
        _Click(screen, "AddEntry");
        await _Until(() => timeEntries.Entries.Count == 1 && badge.Text == "0u25 vandaag", "the added entry and badge, measured plus own");
        Assert.Equal("cockpit", timeEntries.Entries[0].ProjectId);
        _Render(scene.Window, "01-uren-dag.png");

        _Control<TextBox>(screen, "Description").Text = "Uitwerking voorstel";
        _Click(screen, "StartStopwatch");
        await _Until(() => timeEntries.Running is not null && !_Control<Button>(screen, "StartStopwatch").IsEnabled, "one running stopwatch");
        Assert.Contains(_Texts(screen), text => text.Contains("Er loopt al", StringComparison.OrdinalIgnoreCase));
        _Render(scene.Window, "02-uren-dag-stopwatch-lopend.png");

        var sidebar = (sidebarFactory ?? throw new InvalidOperationException("No Tijd sidebar."))();
        var menu = new PluginLauncherButton("Uren", () => { }, badge: badge, iconKind: MaterialIconKind.ClockOutline);
        var sidebarSceneBody = new StackPanel { Spacing = 12, Children = { menu, sidebar } };
        using var sidebarScene = RenderedScene.Show(sidebarSceneBody, 360, 560);
        await _Until(() => _Texts(sidebar).Any(text => text.Contains("Uitwerking voorstel", StringComparison.Ordinal)), "the sidebar stopwatch");
        _Render(sidebarScene.Window, "09-menu-zijbalk.png");
        _Click(sidebar, "StopStopwatch");
        await _Until(() => timeEntries.Running is null && timeEntries.Entries.Count == 2, "the stopped stopwatch entry");

        var previousDay = DateTimeOffset.Now.Date.AddDays(-1);
        timeEntries.Seed(
            new CockpitTimeEntry(Guid.NewGuid(), "Aangeboden regel", previousDay.AddHours(9), TimeSpan.FromMinutes(20), "cockpit", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Offered, previousDay.AddHours(12)),
            new CockpitTimeEntry(Guid.NewGuid(), "Geboekte regel", previousDay.AddHours(10), TimeSpan.FromMinutes(40), "cockpit", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Booked));
        _Click(screen, "PreviousDay");
        await _Until(() => _Texts(screen).Contains("Aangeboden regel") && _Texts(screen).Contains("Geboekte regel"), "the previous day");
        await _Until(() => _Texts(screen).Contains("syn-api"), "the measured time of a day with timeline data");
        Assert.Single(screen.GetVisualDescendants().OfType<Control>(), control => control.Name == "TimelineStrip");
        Assert.Contains("syn-portal", _Texts(screen));

        _Control<TextBox>(screen, "Description").Text = "Vorige dag";
        _Control<TextBox>(screen, "Duration").Text = "0:30";
        _Click(screen, "AddEntry");
        await _Until(() => _Texts(screen).Contains("Vorige dag"), "an earlier open entry");
        Assert.Single(_Buttons(screen, "EditEntry"));
        Assert.Single(_Buttons(screen, "DeleteEntry"));
        _Click(screen, "EditEntry");
        _Control<TextBox>(screen, "Description").Text = "Vorige dag bijgewerkt";
        _Click(screen, "AddEntry");
        await _Until(() => _Texts(screen).Contains("Vorige dag bijgewerkt"), "the edited earlier entry");
        _Render(scene.Window, "03-uren-dag-eerder.png");
        _Click(screen, "DeleteEntry");
        await _Until(() => !_Texts(screen).Contains("Vorige dag bijgewerkt"), "the deleted earlier entry");

        _Click(screen, "Today");
        await _Until(() => _Texts(screen).Contains("Telefonisch overleg met klant XXX"), "returning to today");
        Assert.Equal("0u50 vandaag", badge.Text);
    });

    private static T _Control<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IReadOnlyList<Button> _Buttons(Visual root, string name) =>
        [.. root.GetVisualDescendants().OfType<Button>().Where(button => button.Name == name)];

    private static IReadOnlyList<string> _Texts(Visual root)
    {
        Dispatcher.UIThread.RunJobs();
        return [.. root.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Empty)];
    }

    private static void _Click(Visual root, string name) =>
        _Control<Button>(root, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task _Until(Func<bool> condition, string description)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        Assert.True(condition(), $"Never happened: {description}.");
    }

    private static void _Render(Window window, string fileName)
    {
        if (Environment.GetEnvironmentVariable("PRODUCTIVITY_RENDER_DIR") is not { Length: > 0 } folder)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame to save.");
        frame.Save(Path.Combine(folder, fileName), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private sealed class _TimeEntries : ICockpitTimeEntries
    {
        public List<CockpitTimeEntry> Entries { get; } = [];

        public CockpitRunningStopwatch? Running { get; private set; }

        public Task<CockpitTimeEntry> AddAsync(string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CockpitTimeEntrySource source, CancellationToken cancellationToken = default)
        {
            var entry = new CockpitTimeEntry(Guid.NewGuid(), description, startedAt, duration, projectId, source, CockpitTimeEntryStatus.Open);
            Entries.Add(entry);
            return Task.FromResult(entry);
        }

        public Task<CockpitTimeEntry> UpdateAsync(Guid id, string description, DateTimeOffset startedAt, TimeSpan duration, string? projectId, CancellationToken cancellationToken = default)
        {
            var index = Entries.FindIndex(entry => entry.Id == id);
            var updated = Entries[index] with { Description = description, StartedAt = startedAt, Duration = duration, ProjectId = projectId };
            Entries[index] = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Entries.RemoveAll(entry => entry.Id == id);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CockpitTimeEntry>> ListAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CockpitTimeEntry>>([.. Entries.Where(entry => entry.StartedAt >= from && entry.StartedAt < to)]);

        public Task<CockpitRunningStopwatch> StartStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default)
        {
            if (Running is not null)
            {
                throw new InvalidOperationException("A stopwatch is already running.");
            }

            Running = new CockpitRunningStopwatch(Guid.NewGuid(), description, DateTimeOffset.Now.AddMinutes(-25), projectId);
            return Task.FromResult(Running);
        }

        public Task<CockpitRunningStopwatch> UpdateStopwatchAsync(string description, string? projectId, CancellationToken cancellationToken = default)
        {
            var running = Running ?? throw new InvalidOperationException("No stopwatch is running.");
            Running = running with { Description = description, ProjectId = projectId };
            return Task.FromResult(Running);
        }

        public Task<CockpitTimeEntry> StopStopwatchAsync(CancellationToken cancellationToken = default)
        {
            var running = Running ?? throw new InvalidOperationException("No stopwatch is running.");
            var entry = new CockpitTimeEntry(running.Id, running.Description, running.StartedAt, TimeSpan.FromMinutes(25), running.ProjectId, CockpitTimeEntrySource.Stopwatch, CockpitTimeEntryStatus.Open);
            Running = null;
            Entries.Add(entry);
            return Task.FromResult(entry);
        }

        public Task<CockpitRunningStopwatch?> GetRunningStopwatchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Running);

        public Task MarkOfferedAsync(IReadOnlyCollection<Guid> ids, DateTimeOffset offeredAt, string paneId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SetOutcomeAsync(IReadOnlyCollection<Guid> ids, CockpitTimeEntryOutcome outcome, string? reason = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public void Seed(params CockpitTimeEntry[] entries) => Entries.AddRange(entries);
    }

    // Today: one action three hours ago, ten measured minutes clear of the entries made now. Before today: a
    // morning on two projects, the first half covered by the seeded 09:00 entry, and one agent beside it.
    private sealed class _Timeline : ICockpitTimelineReader
    {
        public Task<CockpitTimeline> QueryAsync(DateTimeOffset from, DateTimeOffset to, CockpitTimelineFilter? filter = null, CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.Now;
            var morning = from.AddHours(9);
            CockpitTimelineLine[] lines = from <= now && now < to
                ? [_Act(now.AddHours(-3), "cockpit")]
                : [
                    .. Enumerable.Range(0, 6).Select(step => _Act(morning.AddMinutes(step * 5), "syn-portal")),
                    _Act(morning.AddHours(2), "syn-api"),
                    _Act(morning.AddHours(2).AddMinutes(5), "syn-api"),
                    new(CockpitTimelineKind.Status, morning, "agent", "syn-api", null, null, CockpitTimelineStatus.Busy, null, null),
                    new(CockpitTimelineKind.Status, morning.AddMinutes(90), "agent", "syn-api", null, null, CockpitTimelineStatus.Idle, null, null),
                ];
            return Task.FromResult(new CockpitTimeline(lines, [], []));
        }

        private static CockpitTimelineLine _Act(DateTimeOffset at, string project) =>
            new(CockpitTimelineKind.Operator, at, "you", project, null, null, null, CockpitOperatorAction.MessageSent, null);
    }

    private sealed class _Loopback : IPluginBackendChannel, IPluginUiChannel
    {
        private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers = new(StringComparer.Ordinal);

        public IDisposable Handle(string action, Func<JsonElement, CancellationToken, Task<JsonElement>> handler)
        {
            _handlers[action] = handler;
            return new _Off(() => _handlers.Remove(action));
        }

        public void Publish(string name, JsonElement payload)
        {
        }

        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.Run(() => _handlers[action](payload, cancellationToken), cancellationToken);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new _Off(() => { });

        private sealed class _Off(Action off) : IDisposable
        {
            public void Dispose() => off();
        }
    }
}

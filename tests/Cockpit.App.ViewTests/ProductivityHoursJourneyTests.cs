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
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;
using Material.Icons;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;
using UiParts = productivityUi::Cockpit.Plugin.Productivity.UI;
using SharedParts = productivityUi::Cockpit.Plugin.Shared.UI;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1722 layer 2 view journey: one real Productivity UI and backend channel over host-owned time entries;
/// AC-1731 adds the measured time from the session timeline beside them; AC-1723 the week proposal and its table.
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
        backend.Storage.Returns(Substitute.For<IPluginStorage>());
        backend.GetProjectsAsync().Returns(Task.FromResult<IReadOnlyList<PluginProjectInfo>>(
        [
            new("syn-portal", "Synvolution portal"),
            new("ai-cockpit", "AI Cockpit"),
            new("syn-api", "Synvolution API"),
            new("depot", "Depot"),
        ]));
        using var plugin = new ProductivityPlugin();
        plugin.Initialize(backend);

        var ui = Substitute.For<ICockpitUiHost>();
        ui.Channel.Returns(channel);
        ui.ActiveSessionWorkingDirectory.Returns(Path.Combine("work", "cockpit"));
        ui.OpenWorkspaceAsync(UiParts.ProductivityUi.WorkspaceTypeId).Returns(Task.CompletedTask);
        ui.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        string? copied = null;
        ui.SetClipboardTextAsync(Arg.Do<string>(text => copied = text)).Returns(Task.CompletedTask);
        var badge = new SideMenuButtonBadge();
        Action? open = null;
        Action? openOverview = null;
        Func<Control>? sidebarFactory = null;
        var menuItems = new List<string>();
        var workspaces = new Dictionary<string, WorkspaceTypeRegistration>(StringComparer.Ordinal);
        ui.AddSideMenuButtonWithBadge("Uren", Arg.Do<Action>(value => open = value), MaterialIconKind.ClockOutline).Returns(badge);
        menuItems.Add("Uren");
        ui.When(host => host.AddSideMenuButton(Arg.Any<string>(), Arg.Any<Action>()))
            .Do(call =>
            {
                menuItems.Add(call.ArgAt<string>(0));
                openOverview = call.ArgAt<Action>(1);
            });
        ui.When(host => host.AddSideMenuSection("Tijd", Arg.Any<Func<Control>>()))
            .Do(call => sidebarFactory = call.Arg<Func<Control>>());
        ui.When(host => host.AddWorkspaceType(Arg.Any<WorkspaceTypeRegistration>()))
            .Do(call =>
            {
                var registration = call.Arg<WorkspaceTypeRegistration>();
                workspaces[registration.Id] = registration;
            });

        new UiParts.ProductivityUi().InitializeUi(ui);
        (open ?? throw new InvalidOperationException("No Uren button."))();
        await ui.Received(1).OpenWorkspaceAsync(UiParts.ProductivityUi.WorkspaceTypeId);
        var registration = workspaces[UiParts.ProductivityUi.WorkspaceTypeId];
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

        _Click(screen, "WeekMode");
        await _Until(() => _Texts(screen).Contains("Voorstel per dag"), "the week proposal");
        Assert.Contains("Schema (grens)", _Texts(screen));
        _Render(scene.Window, "04-uren-week.png");
        _Click(screen, "PreviousDay");
        await _Until(() => _Texts(screen).Any(text => text.StartsWith("Voorstel per dag · week", StringComparison.Ordinal)), "the week before");
        _Render(scene.Window, "05-uren-week-eerder.png");
        _Click(screen, "CopyTable");
        await _Until(() => copied is not null, "the copied table");
        var table = (copied ?? string.Empty).Split(Environment.NewLine);
        string[] header = ["Regel", .. screen.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "OpenDay").Select(button => button.Content as string ?? string.Empty), "Week", "Status"];
        Assert.Equal(header, table[0].Split('\t'));
        Assert.Equal(screen.GetVisualDescendants().OfType<Grid>().Count(grid => grid.Name == "ProposalRow") + 4, table.Length);

        var overviewFrom = _Local(2026, 10, 19);
        timeEntries.Seed(new CockpitTimeEntry(
            Guid.NewGuid(),
            "Weekstart met het team",
            overviewFrom.AddHours(3).AddMinutes(5),
            TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(15)),
            "syn-portal",
            CockpitTimeEntrySource.Duration,
            CockpitTimeEntryStatus.Open));
        var overviewPayload = JsonSerializer.SerializeToElement(new
        {
            From = overviewFrom,
            To = overviewFrom.AddDays(5),
        });
        var overview = await channel.InvokeAsync("productivity.overview.snapshot", overviewPayload);
        var attention = overview.GetProperty("attention");
        Assert.Equal(TimeSpan.FromHours(31), TimeSpan.Parse(attention.GetProperty("yourTime").GetString() ?? string.Empty));
        Assert.Equal(TimeSpan.FromHours(23).Add(TimeSpan.FromMinutes(40)), TimeSpan.Parse(attention.GetProperty("agentTime").GetString() ?? string.Empty));
        Assert.Equal(4, overview.GetProperty("projects").GetArrayLength());
        Assert.Contains(
            overview.GetProperty("timeline").GetProperty("usage").EnumerateArray(),
            usage => usage.GetProperty("inputTokens").GetInt32() > 0 && usage.GetProperty("costUsd").GetDouble() == 0);

        Assert.Equal(["Uren", "Overzicht"], menuItems);
        (openOverview ?? throw new InvalidOperationException("No Overzicht button."))();
        await ui.Received(1).OpenWorkspaceAsync("workspace.productivity.overview");
        var overviewScreen = workspaces["workspace.productivity.overview"].CreateBody(Substitute.For<IWorkspaceContext>());
        using var overviewScene = RenderedScene.Show(overviewScreen, 1280, 900);
        await _Until(
            () => overviewScreen.GetVisualDescendants().OfType<SharedParts.StatTile>().SingleOrDefault(tile => tile.Name == "YourTimeTile")?.Value == "31u00",
            "the week overview");
        Assert.Equal("36u", _Control<SharedParts.StatTile>(overviewScreen, "ScheduleTile").Value);
        Assert.Equal("geen prijs", _Control<SharedParts.StatTile>(overviewScreen, "CostTile").Value);
        Assert.Contains("Overzicht", _Texts(overviewScreen));
        Assert.Contains("Jouw tijd per dag, met de agents ernaast", _Texts(overviewScreen));
        Assert.Contains("Tijd per project", _Texts(overviewScreen));
        Assert.Contains("Eigen regels deze week", _Texts(overviewScreen));
        Assert.Contains("geen prijs", _Texts(overviewScreen));
        _Render(overviewScene.Window, "08-overzicht.png");

        _Click(overviewScreen, "IncludeAgent");
        await _Until(
            () => _Control<SharedParts.StatTile>(overviewScreen, "YourTimeTile").Value == "43u10",
            "the overview including agent time outside your own time");
        _Render(overviewScene.Window, "04-overzicht-plus-agent.png");

        _Control<DatePicker>(overviewScreen, "CustomFrom").SelectedDate = overviewFrom;
        _Control<DatePicker>(overviewScreen, "CustomTo").SelectedDate = overviewFrom;
        _Click(overviewScreen, "ApplyCustomRange");
        await _Until(
            () => _Control<SharedParts.StatTile>(overviewScreen, "YourTimeTile").Value == "12u20",
            "the one-day custom overview");
        Assert.Equal("8u", _Control<SharedParts.StatTile>(overviewScreen, "ScheduleTile").Value);
        Assert.Contains("ma 19 – ma 19 okt 2026", _Texts(overviewScreen));
        _Render(overviewScene.Window, "overzicht-eigen-bereik.png");

        var historicalDay = _Local(2025, 1, 6);
        _Control<DatePicker>(overviewScreen, "CustomFrom").SelectedDate = historicalDay;
        _Control<DatePicker>(overviewScreen, "CustomTo").SelectedDate = historicalDay;
        _Click(overviewScreen, "ApplyCustomRange");
        await _Until(
            () => _Control<SharedParts.StatTile>(overviewScreen, "YourTimeTile").Value == "3u00",
            "the historical aggregate including agent time");
        Assert.Equal("geen prijs", _Control<SharedParts.StatTile>(overviewScreen, "CostTile").Value);
        Assert.Contains("legacy-model", _Texts(overviewScreen));
        Assert.Contains("3u00", _Texts(overviewScreen));

        _Control<DatePicker>(overviewScreen, "CustomFrom").SelectedDate = historicalDay.AddDays(1);
        _Control<DatePicker>(overviewScreen, "CustomTo").SelectedDate = historicalDay;
        _Click(overviewScreen, "ApplyCustomRange");
        await _Until(
            () => _Texts(overviewScreen).Contains("De begindatum moet op of vóór de einddatum liggen."),
            "custom range validation");
        Assert.Contains("ma 6 – ma 6 jan 2025", _Texts(overviewScreen));
        _Control<DatePicker>(overviewScreen, "CustomFrom").SelectedDate = historicalDay;
        _Click(overviewScreen, "ApplyCustomRange");
        await _Until(
            () => overviewScreen.GetVisualDescendants().OfType<SharedParts.StatTile>().SingleOrDefault(tile => tile.Name == "YourTimeTile")?.Value == "3u00",
            "the valid range after validation");

        var alternating = await channel.InvokeAsync(
            "productivity.overview.snapshot",
            JsonSerializer.SerializeToElement(
                new { from = _Local(2026, 2, 2), to = _Local(2026, 2, 3) }));
        var alternatingTickets = alternating.GetProperty("tickets").EnumerateArray().ToDictionary(
            ticket => ticket.GetProperty("ticket").GetString() ?? string.Empty,
            ticket => TimeSpan.Parse(ticket.GetProperty("time").GetString() ?? string.Empty),
            StringComparer.Ordinal);
        Assert.Equal(TimeSpan.FromMinutes(15), alternatingTickets["AC-A"]);
        Assert.Equal(TimeSpan.FromMinutes(5), alternatingTickets["AC-B"]);

        var dst = await channel.InvokeAsync(
            "productivity.overview.snapshot",
            JsonSerializer.SerializeToElement(
                new { from = _Local(2026, 3, 23), to = _Local(2026, 4, 7) }));
        var transitionDay = dst.GetProperty("days").EnumerateArray().Single(day => day.GetProperty("day").GetString() == "2026-03-30");
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            TimeSpan.Parse(transitionDay.GetProperty("yourTime").GetString() ?? string.Empty));
    });

    private static DateTimeOffset _Local(int year, int month, int day)
    {
        var local = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }

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
            if (from.Date == new DateTime(2025, 1, 6))
            {
                return Task.FromResult(new CockpitTimeline(
                    [],
                    [],
                    [new CockpitTimelineDay("legacy-session", new DateOnly(2025, 1, 6), "syn-portal", "legacy-run", ["SYN-100"], TimeSpan.FromHours(2), TimeSpan.Zero, 6, 100, 50, 0, 0, 0, 1, "legacy-model")]));
            }

            if (from.Date == new DateTime(2026, 3, 23))
            {
                return Task.FromResult(new CockpitTimeline([_Act(_Local(2026, 3, 30).AddMinutes(30), "syn-api")], [], []));
            }

            if (from.Date == new DateTime(2026, 2, 2))
            {
                var start = _Local(2026, 2, 2).AddHours(9);
                return Task.FromResult(new CockpitTimeline(
                    [
                        new(CockpitTimelineKind.Operator, start, "you", "ai-cockpit", null, "AC-A", null, CockpitOperatorAction.MessageSent, "AC-A · werk"),
                        new(CockpitTimelineKind.Operator, start.AddMinutes(5), "you", "ai-cockpit", null, "AC-B", null, CockpitOperatorAction.MessageSent, "AC-B · werk"),
                        new(CockpitTimelineKind.Operator, start.AddMinutes(10), "you", "ai-cockpit", null, "AC-A", null, CockpitOperatorAction.MessageSent, "AC-A · werk"),
                    ],
                    [],
                    []));
            }

            if (from.Date == new DateTime(2026, 10, 19))
            {
                return Task.FromResult(_Week43(from, to));
            }

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

        private static CockpitTimeline _Week43(DateTimeOffset from, DateTimeOffset to)
        {
            var monday = _Local(2026, 10, 19);
            var lines = new List<CockpitTimelineLine>();
            _Attention(lines, monday.AddHours(8), 445, "syn-portal", "SYN-412");
            _Attention(lines, monday.AddDays(1).AddHours(8), 582, "ai-cockpit", "AC-1665");
            _Attention(lines, monday.AddDays(2).AddHours(8), 391, "syn-api", "SYN-398");
            _Attention(lines, monday.AddDays(3).AddHours(8), 65, null, null);
            _Attention(lines, monday.AddDays(3).AddHours(10), 82, "depot", "AC-252");
            _Busy(lines, "AC-1665 bouwen", "ai-cockpit", monday.AddDays(1).AddHours(8), monday.AddDays(1).AddHours(17).AddMinutes(42));
            _Busy(lines, "AC-1665 bouwen", "syn-api", monday.AddDays(2).AddHours(8), monday.AddDays(2).AddHours(9).AddMinutes(48));
            _Busy(lines, "SYN-412 tests", "syn-portal", monday.AddDays(4), monday.AddDays(4).AddHours(12).AddMinutes(10));
            CockpitUsageDelta[] usage =
            [
                new("AC-1665 bouwen", monday.AddDays(1).AddHours(18), "run-ac", "claude-opus-4-1", 120000, 22000, 45000, 3000, 121, 18),
                new("SYN-412 tests", monday.AddDays(4).AddHours(13), "run-syn", "claude-sonnet-4-5", 64000, 12000, 18000, 1000, 63.2, 9),
                new("SYN-398 refactor", monday.AddDays(3).AddHours(14), "run-codex", "gpt-5-codex", 48000, 9000, 11000, 0, 0, 7),
            ];
            return new CockpitTimeline(
                [.. lines.Where(line => line.At >= from && line.At < to)],
                [.. usage.Where(value => value.RecordedAt >= from && value.RecordedAt < to)],
                []);
        }

        private static void _Attention(List<CockpitTimelineLine> lines, DateTimeOffset start, int minutes, string? project, string? ticket)
        {
            var count = (int)Math.Ceiling(minutes / 10d);
            var reduction = (count * 10) - minutes;
            var at = start;
            for (var index = 0; index < count; index++)
            {
                lines.Add(new CockpitTimelineLine(
                    CockpitTimelineKind.Operator,
                    at,
                    "you",
                    project,
                    null,
                    ticket,
                    null,
                    CockpitOperatorAction.MessageSent,
                    ticket is null ? null : $"{ticket} · werk"));
                at = at.AddMinutes(index == 0 ? 10 - reduction : 10);
            }
        }

        private static void _Busy(
            List<CockpitTimelineLine> lines,
            string pane,
            string project,
            DateTimeOffset start,
            DateTimeOffset end)
        {
            lines.Add(new CockpitTimelineLine(CockpitTimelineKind.Status, start, pane, project, null, null, CockpitTimelineStatus.Busy, null, null));
            lines.Add(new CockpitTimelineLine(CockpitTimelineKind.Status, end, pane, project, null, null, CockpitTimelineStatus.Idle, null, null));
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

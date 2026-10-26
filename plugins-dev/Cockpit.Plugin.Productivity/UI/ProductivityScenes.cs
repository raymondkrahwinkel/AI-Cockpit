using System.Reflection;
using System.Text.Json;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;

namespace Cockpit.Plugin.Productivity.UI;

// Uren · Dag and Overzicht for `--screenshot --scene plugin:<name>` (AC-1766), fed by a fixed week of Cockpit and
// Depot sessions in worktrees. A dev hook, not a plugin contract: the channel answers from this data, through the
// same overview builder the backend uses.
internal static class ProductivityScenes
{
    private const string Cockpit = "b9f48b5c01694695b1e7f0714ca06ef1";
    private const string Depot = "7a63289f0d6d4870a8d98387dbbeb621";
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 16, 40, 0, TimeSpan.FromHours(1));
    private static readonly PluginProjectInfo[] Projects = [new(Cockpit, "Cockpit"), new(Depot, "Depot"), new("3650ac1d0f2b4c6e9a7d1e5f8b2c4a60", "Synvolution")];

    // Pane, project, ticket, name, profile, model, open; the last has usage from before the timeline only.
    private static readonly (string Pane, string Project, string Ticket, string Name, string Profile, string Model, bool Open)[] Sessions =
    [
        ("310c04a507254a208ad3f985ece5fe8f", Cockpit, "AC-1764", "AC-1764 sidebar-secties", "personal", "opus", true),
        ("e94f3cde6e0e4d76a08f798624fb88d9", Cockpit, "AC-1599", "AC-1599 pr-poort", "Codex", "gpt-5-codex", true),
        ("0289b8a18f9e4393920c8bee7b8eb14b", Cockpit, "AC-1745", "AC-1745 transcript", "Codex", "gpt-5-codex", false),
        ("d47726c7d6a3469cad70694b37425ce6", Depot, "DEP-212", "DEP-212 quota", "work", "sonnet", true),
        ("46fcee8a1d6c4c3eb35dfa4ac000c623", Cockpit, "AC-1737", "AC-1737 host-api", "personal", "opus", false),
    ];

    public static IReadOnlyDictionary<string, Func<(WorkspaceTypeRegistration Registration, string? StatusText)>> Workspaces { get; } =
        new Dictionary<string, Func<(WorkspaceTypeRegistration, string?)>>
        {
            ["productivity-day"] = () =>
            {
                MeasuredTimeCard.OpenFolds = true;
                var host = SceneHost.Create(new SceneChannel());
                var controller = new ProductivityController(host.Channel, new FixedTime());
                controller.UpdateProjectFallback(Cockpit);
                // A scene is built synchronously and rendered at once; the stub channel answers without awaiting.
                controller.RefreshAsync().GetAwaiter().GetResult();
                return (new WorkspaceTypeRegistration(ProductivityUi.WorkspaceTypeId, "Uren", _ => new HoursWorkspaceBody(controller, host)) { IconKind = MaterialIconKind.ClockOutline }, null);
            },
            ["productivity-overview"] = () =>
            {
                var controller = new OverviewController(new SceneChannel(), new FixedTime());
                // A scene is built synchronously and rendered at once; the stub channel answers without awaiting.
                controller.RefreshAsync().GetAwaiter().GetResult();
                return (new WorkspaceTypeRegistration(ProductivityUi.OverviewWorkspaceTypeId, "Overzicht", _ => new OverviewWorkspaceBody(controller, () => { })) { IconKind = MaterialIconKind.ViewDashboardOutline }, null);
            },
        };

    private static IReadOnlyList<OpenCockpitSession> _Open() =>
        [.. Sessions.Where(session => session.Open).Select(session => new OpenCockpitSession(session.Pane, session.Name, session.Profile, "idle"))];

    // Monday to Thursday: you act in three sessions, four agents run beside you, one session left only usage.
    private static CockpitTimeline _Timeline(DateTimeOffset from, DateTimeOffset to)
    {
        var lines = new List<CockpitTimelineLine>();
        var usage = new List<CockpitUsageDelta>();
        for (var day = 0; day < 4; day++)
        {
            var morning = new DateTimeOffset(2026, 10, 26 + day, 0, 0, 0, TimeSpan.FromHours(1));
            void Act(int session, double startHour, int minutes)
            {
                for (var minute = 0; minute < minutes; minute += 5)
                {
                    lines.Add(_Line(CockpitTimelineKind.Operator, morning.AddHours(startHour).AddMinutes(minute), session, null));
                }
            }

            void Busy(int session, double startHour, double hours)
            {
                lines.Add(_Line(CockpitTimelineKind.Status, morning.AddHours(startHour), session, CockpitTimelineStatus.Busy));
                lines.Add(_Line(CockpitTimelineKind.Status, morning.AddHours(startHour + hours), session, CockpitTimelineStatus.Done));
            }

            void Spend(int session, double hour, int tokens, double cost)
            {
                var (pane, _, _, _, profile, model, _) = Sessions[session];
                usage.Add(new CockpitUsageDelta(pane, morning.AddHours(hour), null, model, tokens / 10, tokens / 20, tokens * 3 / 4, tokens / 10, cost, 6) { ProfileLabel = profile });
            }

            Act(0, 9.1 + (day * 0.2), 75 - (day * 10));
            Act(3, 11, 40);
            Act(1, 14, 35 + (day * 5));
            Busy(0, 9, 2.3);
            Busy(2, 10, 2.5 - (day * 0.3));
            Busy(3, 11, 1);
            Busy(1, 13.8, 2.4);
            Spend(0, 11.3, 1_800_000, 12.4);
            Spend(1, 16.2, 2_400_000, 0);
            Spend(2, 12.5, 900_000, 0);
            Spend(3, 12, 600_000, 0);
            Spend(4, 8.5, 400_000, 3.1);
        }

        return new CockpitTimeline(
            [.. lines.Where(line => line.At >= from && line.At < to && line.At <= Now).OrderBy(line => line.At)],
            [.. usage.Where(value => value.RecordedAt >= from && value.RecordedAt < to && value.RecordedAt <= Now)],
            []);
    }

    private static CockpitTimelineLine _Line(CockpitTimelineKind kind, DateTimeOffset at, int session, CockpitTimelineStatus? status)
    {
        var (pane, project, ticket, name, _, _, _) = Sessions[session];
        return new CockpitTimelineLine(kind, at, pane, project, null, ticket, status, kind == CockpitTimelineKind.Operator ? CockpitOperatorAction.MessageSent : null, $"{name} · bezig");
    }

    // Your own lines: one stored with a worktree folder from before AC-1737, one with an id no project has.
    private static IReadOnlyList<CockpitTimeEntry> _Entries(DateTimeOffset from, DateTimeOffset to) =>
    [
        .. new CockpitTimeEntry[]
        {
            new(Guid.Parse("8b0f5b8e-1d2a-4a51-9f1e-0d6c2b7a1e01"), "Telefonisch overleg met klant XXX", new DateTimeOffset(2026, 10, 29, 8, 40, 0, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(15), null, CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Open),
            new(Guid.Parse("8b0f5b8e-1d2a-4a51-9f1e-0d6c2b7a1e02"), "Review PR-poort met Zyra", new DateTimeOffset(2026, 10, 29, 13, 20, 0, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(30), @"C:\Users\raymo\AppData\Roaming\Cockpit\worktrees\308c2a9b28e1\cockpit-personal-e94f3cde", CockpitTimeEntrySource.Stopwatch, CockpitTimeEntryStatus.Open),
            new(Guid.Parse("8b0f5b8e-1d2a-4a51-9f1e-0d6c2b7a1e04"), "Afstemming met de oude projectindeling", new DateTimeOffset(2026, 10, 29, 15, 50, 0, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(10), "3c1f9e0a77d64e0f8a2b5d6c7e8f9a01", CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Open),
            new(Guid.Parse("8b0f5b8e-1d2a-4a51-9f1e-0d6c2b7a1e03"), "Weekstart met het team", new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.FromHours(1)), TimeSpan.FromMinutes(60), Cockpit, CockpitTimeEntrySource.Duration, CockpitTimeEntryStatus.Open),
        }.Where(entry => entry.StartedAt >= from && entry.StartedAt < to),
    ];

    private sealed class SceneChannel : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default)
        {
            object answer = action switch
            {
                ProductivityChannel.Snapshot => _Snapshot(_Read<ProductivitySnapshotRequest>(payload, action)),
                ProductivityChannel.Overview => _Overview(_Read<ProductivityOverviewRequest>(payload, action)),
                _ => throw new NotSupportedException($"The scene does not answer '{action}'."),
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(answer, answer.GetType(), ProductivityChannel.Json));
        }

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();

        private static T _Read<T>(JsonElement payload, string action) =>
            payload.Deserialize<T>(ProductivityChannel.Json) ?? throw new InvalidOperationException($"The '{action}' request was empty.");

        private static ProductivitySnapshot _Snapshot(ProductivitySnapshotRequest request)
        {
            var timeline = _Timeline(request.From, request.To);
            var entries = _Entries(request.From, request.To);
            var day = AttentionTime.Compute(timeline, entries, AttentionSettings.Default, Now < request.To ? Now : request.To);
            return new ProductivitySnapshot(entries, day.YourTime, null, day, Projects, SessionLabel.From(timeline, _Open()));
        }

        private static ProductivityOverviewSnapshot _Overview(ProductivityOverviewRequest request) =>
            ProductivityOverview.Build(
                request,
                _Timeline(request.From, request.To),
                _Entries(request.From, request.To),
                Projects,
                _Open(),
                WeekSettings.Default,
                new Dictionary<string, double> { ["work"] = 3 },
                Now);
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    // Every host member answers its default, except the few the hours view reads while it renders.
    public class SceneHost : DispatchProxy
    {
        private IPluginUiChannel? _channel;

        public static ICockpitUiHost Create(IPluginUiChannel channel)
        {
            var host = DispatchProxy.Create<ICockpitUiHost, SceneHost>();
            ((SceneHost)(object)host)._channel = channel;
            return host;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Channel" => _channel,
            "get_ActiveProjectId" => Cockpit,
            "GetProjectsAsync" => Task.FromResult<IReadOnlyList<PluginProjectInfo>>(Projects),
            _ => _Default(targetMethod?.ReturnType),
        };

        private static object? _Default(Type? type)
        {
            if (type is null || type == typeof(void))
            {
                return null;
            }

            if (type == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = type.GetGenericArguments()[0];
                var fromResult = typeof(Task).GetMethod(nameof(Task.FromResult))
                    ?? throw new MissingMethodException(nameof(Task), nameof(Task.FromResult));
                return fromResult.MakeGenericMethod(result).Invoke(null, [result.IsValueType ? Activator.CreateInstance(result) : null]);
            }

            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }
    }
}

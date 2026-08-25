using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Tracking;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

// The start gate as it is actually wired (AC-345), through the handler the plugin registers; `AutopilotReadyGateTests`
// covers the decision itself. Uses xunit's own Assert, not FluentAssertions (commercially licensed from v8 on).
// Every launch is pointed at a throwaway origin+clone: the plugin's `Directory.GetCurrentDirectory` fallback made epic tests depend on the ambient repository.
public class AutopilotPlanIntentTests : IDisposable
{
    // A bare "origin" plus a clone pushed to it, the same shape GitEpicSubMergeCheckerTests uses — enough for
    // `git log origin/main` to succeed and answer "no commit mentions this sub", which is what these tests need to be
    // true regardless of where they run. `git fetch` inside the checker is best-effort and its failure is ignored.
    private readonly string _origin = Path.Combine(Path.GetTempPath(), $"ac345-origin-{Guid.NewGuid():N}");
    private readonly string _clone = Path.Combine(Path.GetTempPath(), $"ac345-clone-{Guid.NewGuid():N}");

    public AutopilotPlanIntentTests()
    {
        Directory.CreateDirectory(_origin);
        _Git(_origin, "init", "--bare");

        Directory.CreateDirectory(_clone);
        _Git(_clone, "init");
        _Git(_clone, "checkout", "-b", "main");
        _Git(_clone, "remote", "add", "origin", _origin);
        File.WriteAllText(Path.Combine(_clone, "readme.md"), "seed");
        _Git(_clone, "add", "-A");
        _Git(_clone, "-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "-m", "seed commit");
        _Git(_clone, "push", "-u", "origin", "main");
    }

    // Asserted rather than fire-and-forget: a setup step that fails silently would leave the checker unable to answer,
    // which is exactly the "epic-paused instead of planning" symptom this wiring exists to remove — it must surface as
    // a broken fixture, not as a mysterious assertion failure further down.
    private static void _Git(string directory, params string[] arguments)
    {
        var result = GitCommandLine.RunAsync("git", arguments, directory).GetAwaiter().GetResult();
        Assert.True(result.Ok, $"git {string.Join(' ', arguments)} failed: {result.Error}");
    }

    public void Dispose()
    {
        _TryDelete(_origin);
        _TryDelete(_clone);
        GC.SuppressFinalize(this);
    }

    // Same shape as GitEpicSubMergeCheckerTests._TryDelete, and for the same reason: git marks its object files
    // read-only, so a plain recursive delete throws UnauthorizedAccessException on Windows and would fail a run whose
    // assertions all passed.
    private static void _TryDelete(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // A throwaway directory under the system temp folder.
        }
    }

    private sealed class FakeStorage : IPluginStorage
    {
        private readonly Dictionary<string, string> _data = new(StringComparer.Ordinal);

        public T? Get<T>(string key) => _data.TryGetValue(key, out var json) ? JsonSerializer.Deserialize<T>(json) : default;

        public void Set<T>(string key, T value) => _data[key] = JsonSerializer.Serialize(value);

        public void SetSecret(string key, string value) => Set(key, value);

        public string? GetSecret(string key) => Get<string>(key);
    }

    private sealed class RecordingTracker : ITrackerProvider
    {
        private readonly Dictionary<string, List<TrackerLinkedIssue>> _links = new(StringComparer.OrdinalIgnoreCase);

        public string TrackerId => "youtrack";

        public List<(string IssueId, string Comment)> Comments { get; } = [];

        public void AddChild(string epicId, string childId, string title, string stage) =>
            _Add(epicId, new TrackerLinkedIssue("parent for", TrackerLinkDirection.Outward, childId, title, stage));

        private void _Add(string issueId, TrackerLinkedIssue link)
        {
            if (!_links.TryGetValue(issueId, out var list))
            {
                list = [];
                _links[issueId] = list;
            }

            list.Add(link);
        }

        public Task<IReadOnlyList<TrackerLinkedIssue>> GetLinkedIssuesAsync(string issueId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackerLinkedIssue>>(_links.TryGetValue(issueId, out var list) ? list : []);

        public Task<bool> PostCommentAsync(string issueId, string comment, CancellationToken cancellationToken = default)
        {
            Comments.Add((issueId, comment));
            return Task.FromResult(true);
        }

        public Task<bool> SetStageAsync(string issueId, string stage, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> AttachAsync(string issueId, string fileName, byte[] content, string mediaType, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string issueId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackerComment>>([]);
    }

    private (Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>> Handler, RecordingTracker Tracker) Started()
    {
        var storage = new FakeStorage();
        // Without a CEO profile the handler refuses before it ever reaches the gate, so the run this test is about
        // would never happen.
        new AutopilotSettings(storage).SetCeoProfileLabel("work");

        var tracker = new RecordingTracker();
        var host = Substitute.For<ICockpitHost>();
        host.Storage.Returns(storage);
        host.TrackerProviders.Returns([tracker]);
        host.RegisteredAutopilotTemplates.Returns([]);
        // The directory the plugin runs its merge check in (AutopilotPlugin: active session's directory, else the
        // process's own). Naming the test's own repository here is what keeps the epic tests from answering
        // differently depending on where the run happens to sit.
        host.Sessions.ActiveSessionWorkingDirectory.Returns(_clone);
        host.OpenWorkspaceAsync(Arg.Any<string>()).Returns(Task.CompletedTask);
        host.ShowSettingsAsync().Returns(Task.CompletedTask);

        Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>? handler = null;
        host.When(candidate => candidate.RegisterIntentHandler("plan", Arg.Any<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>()))
            .Do(call => handler = call.Arg<Func<PluginIntent, Task<IReadOnlyDictionary<string, string>>>>());

        new AutopilotPlugin().Initialize(host);

        Assert.NotNull(handler);
        return (handler!, tracker);
    }

    private static PluginIntent Plan(string stage, string title = "A title", string caller = "youtrack") =>
        new(caller, "autopilot", "plan", new Dictionary<string, string>
        {
            ["tracker"] = "youtrack",
            ["issue"] = "AC-345",
            ["title"] = title,
            ["stage"] = stage,
        });

    [Fact]
    public async Task Plan_OnAnItemThatIsNotOnTheExecutableStage_RefusesAndCommentsWhy()
    {
        var (handler, tracker) = Started();

        var result = await handler(Plan("Backlog"));

        Assert.Equal("not-ready", result["status"]);
        Assert.Equal("AC-345", result["issue"]);
        var (issueId, comment) = Assert.Single(tracker.Comments);
        Assert.Equal("AC-345", issueId);
        Assert.Contains("Ready", comment);
    }

    [Fact]
    public async Task Plan_OnAReadyItem_GoesThroughToPlanning()
    {
        var (handler, tracker) = Started();

        var result = await handler(Plan("Ready"));

        Assert.Equal("planning", result["status"]);
        Assert.Empty(tracker.Comments);
    }

    [Fact]
    public async Task Plan_OnTheSameRefusedItemTwice_WritesTheReasonOnce()
    {
        var (handler, tracker) = Started();

        await handler(Plan("Backlog"));
        await handler(Plan("Backlog"));

        Assert.Single(tracker.Comments);
    }

    [Fact]
    public async Task Plan_FromAPluginThatIsNotTheTracker_RefusesWithoutWritingOnTheIssue()
    {
        // Any installed plugin may send an intent, and the payload names its own tracker and issue. Commenting on
        // whatever it names would hand every plugin a way to write on arbitrary issues with the operator's token.
        var (handler, tracker) = Started();

        var result = await handler(Plan("Backlog", caller: "some-other-plugin"));

        Assert.Equal("not-ready", result["status"]);
        Assert.Empty(tracker.Comments);
    }

    [Fact]
    public async Task Plan_OnABrainstormItem_IsRefusedEvenOnTheExecutableStage()
    {
        var (handler, _) = Started();

        var result = await handler(Plan("Ready", title: "[Brainstorm] should the CEO validate twice?"));

        Assert.Equal("not-ready", result["status"]);
    }

    // AC-346: an epic click (the same "plan" intent, on an item that turns out to have "parent for" children) never
    // reaches the pipeline as the epic itself — AutopilotEpicRunner swaps it for the sub it picked before the stage
    // gate above ever runs.

    [Fact]
    public async Task Plan_OnAnEpicWithAReadySub_GoesThroughToPlanningOnTheSub_NotTheEpic()
    {
        var (handler, tracker) = Started();
        // The merge check runs against this class's own throwaway origin/main, whose only commit is "seed commit" —
        // so no sub id can read as already merged, and no ambient repository can decide this test. The distinctive
        // id is kept anyway: it costs nothing and keeps the assertion honest.
        tracker.AddChild("AC-345", "ZZ-999901", "The first sub", "Ready");

        var result = await handler(Plan("Backlog")); // the epic's own stage is irrelevant — only the sub's is checked

        Assert.Equal("planning", result["status"]);
        Assert.Equal("ZZ-999901", result["issue"]);
        Assert.Empty(tracker.Comments);
    }

    [Fact]
    public async Task Plan_OnAnEpicWhoseNextSubIsNotReady_PausesTheChainAndCommentsOnTheEpic_NotTheSub()
    {
        var (handler, tracker) = Started();
        tracker.AddChild("AC-345", "ZZ-999902", "The first sub", "Backlog");

        var result = await handler(Plan("Backlog"));

        Assert.Equal("epic-paused", result["status"]);
        Assert.Equal("AC-345", result["issue"]);
        Assert.Equal("ZZ-999902", result["sub"]);
        var (commentedId, comment) = Assert.Single(tracker.Comments);
        Assert.Equal("AC-345", commentedId);
        Assert.Contains("ZZ-999902", comment);
    }

    [Fact]
    public async Task Plan_OnAnItemWithNoChildren_IsUnaffectedByTheEpicCheck()
    {
        // Regression: AC-345's behaviour for a plain issue is untouched by the AC-346 epic lookup ahead of it.
        var (handler, tracker) = Started();

        var result = await handler(Plan("Ready"));

        Assert.Equal("planning", result["status"]);
        Assert.Equal("AC-345", result["issue"]);
        Assert.Empty(tracker.Comments);
    }
}

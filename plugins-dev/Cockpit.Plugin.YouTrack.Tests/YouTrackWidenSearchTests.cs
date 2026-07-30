using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The free-text search's own widen-past-the-loaded-page behaviour (AC-518 follow-up, Raymond: "als er meer dan
/// 100 zijn, moet het alsnog doorzoekbaar en vindbaar zijn"). Driven over a real HTTP round trip, same reasoning
/// as <see cref="YouTrackStateFilterServerSideTests"/>: the trigger this proves lives behind a real async fetch,
/// which cannot be driven inside a synchronous <see cref="HeadlessAvalonia.Run"/> body.
/// <para>
/// The corrected trigger (Raymond's own correction mid-ticket): a truncated load's client-side hits are NOT proof
/// of completeness, so the widen fires whenever <c>_all.Count == MaxResults</c>, regardless of how many local
/// hits already show — not "only when local filtering finds nothing", which was this fix's own first, wrong draft.
/// </para>
/// </summary>
[Collection("avalonia")]
public class YouTrackWidenSearchTests
{
    [Fact]
    public async Task TruncatedListWithLocalHits_StillWidensViaTheServer()
    {
        // The scenario the corrected trigger exists for: "login" already matches one of the MaxResults loaded
        // rows, which reads as a complete answer and is not one — there could be more beyond the loaded page.
        var issueQueries = new ConcurrentQueue<string>();
        await using var harness = await _OpenAsync(issueQueries);

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", number == 1 ? "Fix the login bug" : $"Issue {number}", null, "AT", "Open"))
            .ToArray();
        harness.PlantAllAndFilter(issues, "login");

        var requestsBefore = issueQueries.Count;
        await harness.WidenSearchAsync();

        Assert.True(issueQueries.Count > requestsBefore, "a truncated list's local hits are not proof of completeness — this must still widen");
    }

    [Fact]
    public async Task NonTruncatedListWithNoLocalHits_DoesNotWiden()
    {
        // Fewer than MaxResults loaded issues means the client-side filter already saw everything there is —
        // a real zero, not a truncation artifact — so a server call would add nothing.
        var issueQueries = new ConcurrentQueue<string>();
        await using var harness = await _OpenAsync(issueQueries);

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults - 1)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", $"Issue {number}", null, "AT", "Open"))
            .ToArray();
        harness.PlantAllAndFilter(issues, "no such term anywhere");

        var requestsBefore = issueQueries.Count;
        await harness.WidenSearchAsync();

        Assert.Equal(requestsBefore, issueQueries.Count);
    }

    [Fact]
    public async Task TruncatedListWithEmptyQuery_DoesNotWiden()
    {
        // Nothing to search for — the normal load route (not this widen path) is what governs an empty search box.
        var issueQueries = new ConcurrentQueue<string>();
        await using var harness = await _OpenAsync(issueQueries);

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", $"Issue {number}", null, "AT", "Open"))
            .ToArray();
        harness.PlantAllAndFilter(issues, string.Empty);

        var requestsBefore = issueQueries.Count;
        await harness.WidenSearchAsync();

        Assert.Equal(requestsBefore, issueQueries.Count);
    }

    [Fact]
    public async Task WidenSearch_CombinesWithTheActiveState()
    {
        // Requirement 2: a state is already chosen, so the widen must not surface issues from a stage the state
        // filter itself excludes — verified end to end against the literal query text the client sends.
        var issueQueries = new ConcurrentQueue<string>();
        await using var harness = await _OpenAsync(issueQueries);
        harness.SetResolvedState("State", "Ready");

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", "Fix the login bug", null, "AT", "Ready"))
            .ToArray();
        harness.PlantAllAndFilter(issues, "login");

        await harness.WidenSearchAsync();

        // No project prefix: this harness's /api/admin/projects always answers "[]" (empty on purpose — the widen
        // path is what is under test here, not project resolution, already covered by YouTrackStateFilterServerSideTests),
        // so the project filter never leaves "All" and BuildQuery omits the project: clause.
        var decoded = Uri.UnescapeDataString(issueQueries.Last());
        Assert.Contains("query=#Unresolved State: {Ready} \"login\"", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulWiden_ReplacesTheGridAndReportsItInTheStatusLine()
    {
        var issueQueries = new ConcurrentQueue<string>();
        var found = new YouTrackIssue("9-1", "AT-9", "Login bug found beyond page one", null, "AT", "Open");
        await using var harness = await _OpenAsync(issueQueries, issuesResponse: $$"""
            [{"id":"{{found.Id}}","idReadable":"{{found.IdReadable}}","summary":"{{found.Summary}}","project":{"shortName":"AT"},"customFields":[]}]
            """);

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", $"Issue {number}", null, "AT", "Open"))
            .ToArray();
        harness.PlantAllAndFilter(issues, "login");

        await harness.WidenSearchAsync();

        var gridItems = harness.GridItems();
        var status = harness.StatusText();
        Assert.Single(gridItems);
        Assert.Equal("AT-9", gridItems[0].IdReadable);
        Assert.Contains("found on the server", status, StringComparison.Ordinal);
        Assert.Contains("login", status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedServerSearch_ReportsFailure_WithoutChangingTheGrid()
    {
        var issueQueries = new ConcurrentQueue<string>();
        await using var harness = await _OpenAsync(issueQueries, issuesStatusCode: 500);

        var issues = Enumerable.Range(1, YouTrackDialogControl.MaxResults)
            .Select(number => new YouTrackIssue($"1-{number}", $"AT-{number}", number == 1 ? "Fix the login bug" : $"Issue {number}", null, "AT", "Open"))
            .ToArray();
        harness.PlantAllAndFilter(issues, "login");

        var localHits = harness.GridItems();
        await harness.WidenSearchAsync();

        // Never worse than today: a failed widen leaves whatever the client-side filter already found in place —
        // it does not empty the grid or replace it with something misleading.
        Assert.Equal(localHits.Select(issue => issue.IdReadable), harness.GridItems().Select(issue => issue.IdReadable));
        Assert.Contains("Could not search the server", harness.StatusText(), StringComparison.Ordinal);
    }

    private static async Task<Harness> _OpenAsync(ConcurrentQueue<string> issueQueries, string issuesResponse = "[]", int issuesStatusCode = 200)
    {
        var server = await LoopbackHttpServer.StartAsync(context => _AnswerAsync(context, issueQueries, issuesResponse, issuesStatusCode));
        var instance = new YouTrackInstance("Remote", $"{server.BaseUrl}api", "perm-token", string.Empty);

        YouTrackDialogControl? dialog = null;
        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());
            window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();
        });

        var harness = new Harness(dialog!, window!, server);

        // Waits for the constructor's own automatic instance-change/load chain to settle before the test starts
        // planting its own _all — otherwise that background fetch could overwrite the test's planted state, or
        // (worse) still be in flight when the test's own widen call fires, landing its /api/issues request in the
        // same queue and making "how many requests did the widen add" unreliable.
        await harness.WaitForInitialLoadAsync(issueQueries);
        return harness;
    }

    private static Task _AnswerAsync(HttpContext context, ConcurrentQueue<string> issueQueries, string issuesResponse, int issuesStatusCode)
    {
        var path = context.Request.Path.Value;
        if (path == "/api/issues")
        {
            issueQueries.Enqueue(context.Request.QueryString.Value ?? string.Empty);
            context.Response.StatusCode = issuesStatusCode;
            return issuesStatusCode == 200 ? context.Response.WriteAsync(issuesResponse) : Task.CompletedTask;
        }

        if (path == "/api/admin/projects")
        {
            return context.Response.WriteAsync("[]");
        }

        throw new InvalidOperationException($"Unexpected request: {path}");
    }

    /// <summary>One dialog, wired to a real loopback server, with reflection access to the private members this widen-search behaviour lives behind.</summary>
    private sealed class Harness(YouTrackDialogControl dialog, Window window, LoopbackHttpServer server) : IAsyncDisposable
    {
        // Waits for the constructor's own automatic /api/issues request to have both gone out AND been processed.
        // Polling the status line alone is not enough: right after Window.Show() it can read as neither "Loading…"
        // (the async chain has an await — GetProjectsAsync — before it ever sets that) nor a finished message, which
        // made an earlier version of this wait return immediately, before the real request had even been sent —
        // and that request then landed, arbitrarily late, in the same queue a test's own widen call was measuring.
        public async Task WaitForInitialLoadAsync(ConcurrentQueue<string> issueQueries)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (issueQueries.Count > 0 && !string.Equals(StatusText(), "Loading…", StringComparison.Ordinal))
                {
                    return;
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The dialog's initial load never reached /api/issues.");
        }

        public void PlantAllAndFilter(YouTrackIssue[] issues, string searchText) => Dispatcher.UIThread.Invoke(() =>
        {
            _Field("_all").SetValue(dialog, (IReadOnlyList<YouTrackIssue>)issues);
            _Search().Text = searchText;
            _Method("_ApplyFilter").Invoke(dialog, []);
        });

        public void SetResolvedState(string fieldName, string selectedState) => Dispatcher.UIThread.Invoke(() =>
        {
            // _stateFieldName set FIRST, matching the real production order (_ResolveStateFieldAsync) rather than
            // working around it: the ComboBox mutations below are real Avalonia event plumbing and each fires
            // SelectionChanged on its own, which — with _stateFieldName already non-null — would otherwise take
            // _OnStateFilterChangedAsync's reload branch and race the widen call this test means to isolate. Safe
            // now under the real production order because _isPopulatingStateOptions — the same guard
            // _SetStateOptions itself sets around this exact pair of assignments — tells _OnStateFilterChangedAsync
            // this is a dropdown rebuild, not the operator's own choice, so no reload fires (AC-518 adversarial
            // review fix). Toggled directly here, rather than routed through _SetStateOptions itself, only so the
            // test keeps full control of exactly which state ends up selected instead of inheriting that method's
            // own previous-selection-preserving logic.
            _Field("_stateFieldName").SetValue(dialog, fieldName);

            var isPopulatingStateOptions = _Field("_isPopulatingStateOptions");
            isPopulatingStateOptions.SetValue(dialog, true);
            try
            {
                _StateFilter().ItemsSource = new List<string> { "All", selectedState };
                _StateFilter().SelectedItem = selectedState;
            }
            finally
            {
                isPopulatingStateOptions.SetValue(dialog, false);
            }
        });

        public async Task WidenSearchAsync() => await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var task = (Task)_Method("_WidenSearchIfTruncatedAsync").Invoke(dialog, [])!;
            await task;
        });

        public List<YouTrackIssue> GridItems() => Dispatcher.UIThread.Invoke(() =>
            (_Grid().ItemsSource as ObservableCollection<YouTrackIssue>)?.ToList() ?? []);

        // Render-proof (same idiom as the MaxResults truncation notice): walks the shown window's real visual
        // tree by the status TextBlock's Name, rather than reading the private field directly — that way a
        // regression that stops the status text from ever reaching a *mounted* control is not masked by reflection
        // finding some TextBlock instance that was simply never shown.
        public string StatusText() => Dispatcher.UIThread.Invoke(() =>
            window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text => text.Name == "status")?.Text ?? string.Empty);

        private TextBox _Search() => (TextBox)_Field("_search").GetValue(dialog)!;

        private ComboBox _StateFilter() => (ComboBox)_Field("_stateFilter").GetValue(dialog)!;

        private DataGrid _Grid() => (DataGrid)_Field("_grid").GetValue(dialog)!;

        private static FieldInfo _Field(string name) =>
            typeof(YouTrackDialogControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"YouTrackDialogControl no longer has a field named {name}.");

        private static MethodInfo _Method(string name) =>
            typeof(YouTrackDialogControl).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"YouTrackDialogControl no longer has a method named {name}.");

        public ValueTask DisposeAsync() => server.DisposeAsync();
    }
}

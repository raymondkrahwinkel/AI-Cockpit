using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

// The dialog's state dropdown, sourced from the project's own status field (AC-518) rather than from whichever
// issues happened to load — driven over a real HTTP round trip, the one path `YouTrackDialogControlTests`
// deliberately avoids (its harness plants `_all` directly and gives every instance a blank token so the
// dialog's own fetch short-circuits before it ever awaits anything real).
//
// A real fetch cannot be driven inside a synchronous `HeadlessAvalonia.Run` body: the dialog kicks its
// load off from an async-void event handler, and its continuation only runs once that body has returned control to
// Avalonia's own dispatcher loop. So this test starts the dialog via `Dispatcher.UIThread.InvokeAsync`
// (which does not block the loop) and then polls, from the test's own thread, until the fetch has settled.
[Collection("avalonia")]
public class YouTrackStateFilterServerSideTests
{
    [Fact]
    public async Task StateDropdown_ComesFromTheProjectsField_NotFromTheOneLoadedRow()
    {
        var issueQueries = new ConcurrentQueue<string>();
        await using var server = await LoopbackHttpServer.StartAsync(context => _AnswerAsync(context, issueQueries));
        var instance = new YouTrackInstance("Remote", $"{server.BaseUrl}api", "perm-token", "AC");

        ComboBox? stateFilter = null;
        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            var dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());
            window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();

            stateFilter = typeof(YouTrackDialogControl).GetField("_stateFilter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as ComboBox
                ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its state filter in _stateFilter.");
        });

        var options = await _WaitForAsync(() => _Options(stateFilter!), found => found.Count > 2, TimeSpan.FromSeconds(5));

        // The one row this test serves from /api/issues carries only "Open" — "All" + "Open" is two options.
        // Getting three states back proves the dropdown asked the project's own field, not the loaded row.
        Assert.Equal(["All", "Open", "Ready", "Test"], options);

        // IL#9: proves the values are actually rendered, not merely present in ItemsSource — opens the dropdown's
        // own popup (a separate visual root, so GetVisualDescendants() on the window would miss it — AvaloniaUI.md)
        // and reads back the realized container for "Ready" and "Test", the two values the loaded row never carried.
        var renderedLabels = Dispatcher.UIThread.Invoke(() =>
        {
            stateFilter!.IsDropDownOpen = true;
            window!.UpdateLayout();

            var labels = Enumerable.Range(0, options.Count)
                .Select(index => stateFilter.ContainerFromIndex(index) as ComboBoxItem)
                .Select(container => container?.Content as string ?? string.Empty)
                .ToList();

            stateFilter.IsDropDownOpen = false;
            return labels;
        });

        Assert.Equal(options, renderedLabels);
    }

    [Fact]
    public async Task ChoosingAState_ScopesTheReloadToThatValueOnTheResolvedField()
    {
        var issueQueries = new ConcurrentQueue<string>();
        await using var server = await LoopbackHttpServer.StartAsync(context => _AnswerAsync(context, issueQueries));
        var instance = new YouTrackInstance("Remote", $"{server.BaseUrl}api", "perm-token", "AC");

        ComboBox? stateFilter = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            var dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());
            var window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();

            stateFilter = typeof(YouTrackDialogControl).GetField("_stateFilter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as ComboBox
                ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its state filter in _stateFilter.");
        });

        await _WaitForAsync(() => _Options(stateFilter!), found => found.Count > 2, TimeSpan.FromSeconds(5));

        var queriesBeforeChoosingState = issueQueries.Count;

        // Filling the dropdown assigns ItemsSource and SelectedItem, and both raise SelectionChanged. Without the
        // populating guard that echo is read as an operator choice and fires reloads of its own, which then race
        // the real one. Pinning the count here is what proves the guard removes those fetches rather than merely
        // letting a generation counter discard their answers — assert on the grid alone and the guard can be
        // deleted with the suite still green.
        Assert.Equal(1, queriesBeforeChoosingState);

        Dispatcher.UIThread.Invoke(() => stateFilter!.SelectedItem = "Ready");

        var newQuery = await _WaitForQueryAsync(issueQueries, queriesBeforeChoosingState, TimeSpan.FromSeconds(5));

        // Uri.EscapeDataString turns the space in "Ready" into %20 and the colon/braces into their own escapes —
        // decode before asserting on the literal filter text rather than the wire form.
        var decoded = Uri.UnescapeDataString(newQuery);
        Assert.Contains("query=project:AC #Unresolved State: {Ready}", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingAState_OnAMultiWordStatusField_BracesTheFieldNameInTheSentQuery()
    {
        // EJ's real project uses "Kanban State" rather than "State" (StateFieldNames, YouTrackFieldParser) — the
        // adversarial-review defect this reproduces: only the value ever got braced ("Kanban State: {Ready}"),
        // which YouTrack reads as two tokens rather than one field:value pair once the field name itself has a space.
        var issueQueries = new ConcurrentQueue<string>();
        await using var server = await LoopbackHttpServer.StartAsync(context => _AnswerWithKanbanStateAsync(context, issueQueries));
        var instance = new YouTrackInstance("Remote", $"{server.BaseUrl}api", "perm-token", "AC");

        ComboBox? stateFilter = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            var dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());
            var window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();

            stateFilter = typeof(YouTrackDialogControl).GetField("_stateFilter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as ComboBox
                ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its state filter in _stateFilter.");
        });

        await _WaitForAsync(() => _Options(stateFilter!), found => found.Count > 2, TimeSpan.FromSeconds(5));

        // The dropdown's own population can echo a redundant, unguarded reload whose request may still be in
        // flight the instant options.Count first crosses the threshold above — see the comment on
        // SelectingAState_WhileTheRedundantPopulationTriggeredReloadIsStillInFlight for why. A settle grace period
        // here (same value that test uses for the same reason) lets any such echo land before the baseline is
        // pinned, so a slow-to-arrive echo on a busy runner cannot be mistaken for the state-driven reload below —
        // without it, this flaked in CI: the wait after choosing "Ready" returned the echo instead.
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        var queriesBeforeChoosingState = issueQueries.Count;
        Dispatcher.UIThread.Invoke(() => stateFilter!.SelectedItem = "Ready");

        var newQuery = await _WaitForQueryAsync(issueQueries, queriesBeforeChoosingState, TimeSpan.FromSeconds(5));

        var decoded = Uri.UnescapeDataString(newQuery);
        Assert.Contains("query=project:AC #Unresolved {Kanban State}: {Ready}", decoded, StringComparison.Ordinal);
    }

    private static Task _AnswerWithKanbanStateAsync(HttpContext context, ConcurrentQueue<string> issueQueries)
    {
        var path = context.Request.Path.Value;
        if (path == "/api/issues")
        {
            issueQueries.Enqueue(context.Request.QueryString.Value ?? string.Empty);
        }

        var body = path switch
        {
            "/api/admin/projects" => """[{"shortName":"AC","name":"AI Cockpit"}]""",
            "/api/admin/projects/AC/customFields" =>
                """[{"field":{"name":"Kanban State"},"bundle":{"values":[{"name":"Open"},{"name":"Ready"},{"name":"Test"}]}}]""",
            "/api/issues" =>
                """[{"id":"1-1","idReadable":"AC-1","summary":"One row","description":null,"project":{"shortName":"AC"},"customFields":[{"name":"Kanban State","$type":"StateIssueCustomField","value":{"name":"Open"}}]}]""",
            var other => throw new InvalidOperationException($"Unexpected request: {other}"),
        };

        return context.Response.WriteAsync(body);
    }

    // Reproduces the adversarial-review blocker (AC-518): _ResolveStateFieldAsync sets _stateFieldName BEFORE
    // calling _SetStateOptions, which assigns both ItemsSource and SelectedItem on _stateFilter — real Avalonia
    // ComboBox mutations, each of which fires SelectionChanged. With _stateFieldName already non-null at that
    // point, _OnStateFilterChangedAsync takes the "reload" branch for both, so the project-driven resolution
    // alone kicks off a redundant, unfiltered ("All") reload nobody asked for, racing the explicit load that
    // follows it in _OnInstanceChangedAsync/_OnProjectChangedAsync. Neither carries a reentrancy guard or a
    // generation token, so whichever response lands last wins, regardless of whether it is still the current
    // choice. This test lets the operator choose a real, different state ("In Progress") while that redundant
    // "All" fetch is still in flight (deliberately slow-walked by the fake server) and proves the grid still
    // shows only "In Progress" issues once the slow response has had time to land — i.e. the stale, broader
    // fetch must not be allowed to overwrite what the operator's own choice already asked for.
    [Fact]
    public async Task SelectingAState_WhileTheRedundantPopulationTriggeredReloadIsStillInFlight_DoesNotLoseTheChosenState()
    {
        var issueQueries = new ConcurrentQueue<string>();
        await using var server = await LoopbackHttpServer.StartAsync(context => _AnswerWithRaceAsync(context, issueQueries));
        var instance = new YouTrackInstance("Remote", $"{server.BaseUrl}api", "perm-token", "AC");

        YouTrackDialogControl? dialog = null;
        ComboBox? stateFilter = null;
        Window? window = null;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var settings = new YouTrackSettings(new InMemoryPluginStorage()) { Instances = [instance] };
            var host = new FakeCockpitHost();
            var links = new SessionIssueLinks(host);
            dialog = new YouTrackDialogControl(settings, host, links, new IssueStateChanges());
            window = new Window { Width = 1280, Height = 860, Content = dialog };
            window.Show();

            stateFilter = typeof(YouTrackDialogControl).GetField("_stateFilter", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(dialog) as ComboBox
                ?? throw new InvalidOperationException("YouTrackDialogControl no longer keeps its state filter in _stateFilter.");
        });

        // The project's status field resolves independently of the (deliberately slow) /api/issues fetches, so
        // this settles well before either the redundant "All" reload or the operator's own choice below land.
        await _WaitForAsync(() => _Options(stateFilter!), found => found.Count > 2, TimeSpan.FromSeconds(5));

        // Coverage gap closed (adversarial re-review): the assertions below this point only ever proved the
        // final GRID content, which _loadToken's generation counter alone already guarantees — removing
        // _isPopulatingStateOptions entirely (and so letting _SetStateOptions's ComboBox mutations fire a
        // redundant "All" reload, exactly the AC-518 blocker this test's own name describes) leaves the suite
        // green, because the stale response still loses the _loadToken race. Pinning the REQUEST COUNT here is
        // what actually proves the guard suppresses that redundant fetch rather than merely letting a later one
        // win: one deliberate load (_OnInstanceChangedAsync/_OnProjectChangedAsync's explicit _LoadIssuesAsync
        // call) should have reached the server by now, and no "echo" from the dropdown population alongside it.
        await _WaitForQueryAsync(issueQueries, 0, TimeSpan.FromSeconds(5));

        // A guard-regression's echo request is enqueued (by the server, on arrival) essentially back-to-back
        // with the deliberate one above, not gated behind the slow path's 300ms response delay — this grace
        // period gives it time to land before the count below is pinned, so a reintroduced redundant fetch
        // cannot slip through as "just hasn't arrived yet".
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        Assert.Single(issueQueries);

        // The operator's own real action: choose a different, real state while the population-triggered "All"
        // reload (fired off by _SetStateOptions above, before this test ever touched the dropdown) is still
        // in flight.
        Dispatcher.UIThread.Invoke(() => stateFilter!.SelectedItem = "In Progress");

        // Long enough for the fake server's deliberately slow "All" response(s) to land and, on the racy code,
        // overwrite _all after the operator's own filtered fetch already resolved.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        var gridItems = Dispatcher.UIThread.Invoke(() =>
        {
            var grid = (DataGrid)typeof(YouTrackDialogControl).GetField("_grid", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
            return (grid.ItemsSource as ObservableCollection<YouTrackIssue>)?.ToList() ?? [];
        });

        Assert.True(gridItems.Count > 0 && gridItems.All(issue => issue.State == "In Progress"),
            $"expected only \"In Progress\" issues once the slow \"All\" response had time to land, got: " +
            $"{string.Join(", ", gridItems.Select(issue => $"{issue.IdReadable}:{issue.State}"))}");
    }

    private static async Task _AnswerWithRaceAsync(HttpContext context, ConcurrentQueue<string> issueQueries)
    {
        var path = context.Request.Path.Value;
        if (path == "/api/admin/projects")
        {
            await context.Response.WriteAsync("""[{"shortName":"AC","name":"AI Cockpit"}]""");
            return;
        }

        if (path == "/api/admin/projects/AC/customFields")
        {
            await context.Response.WriteAsync(
                """[{"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"In Progress"},{"name":"Done"}]}}]""");
            return;
        }

        if (path == "/api/issues")
        {
            var query = context.Request.QueryString.Value ?? string.Empty;
            issueQueries.Enqueue(query);
            var decoded = Uri.UnescapeDataString(query);

            if (decoded.Contains("State:", StringComparison.Ordinal))
            {
                // The operator's own choice: answered immediately, so it is the first to land.
                await context.Response.WriteAsync(
                    """[{"id":"2-1","idReadable":"AC-9","summary":"In progress issue","project":{"shortName":"AC"},"customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"In Progress"}}]}]""");
                return;
            }

            // The redundant, unfiltered reload the population echo (AC-518 blocker) kicks off — deliberately
            // slow so it lands after the operator's own filtered fetch above, the exact ordering the failure
            // scenario needs.
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            await context.Response.WriteAsync(
                """
                [{"id":"1-1","idReadable":"AC-1","summary":"Open issue","project":{"shortName":"AC"},"customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"Open"}}]},
                {"id":"1-2","idReadable":"AC-2","summary":"Done issue","project":{"shortName":"AC"},"customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"Done"}}]}]
                """);
            return;
        }

        throw new InvalidOperationException($"Unexpected request: {path}");
    }

    private static async Task<string> _WaitForQueryAsync(ConcurrentQueue<string> issueQueries, int alreadySeen, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = issueQueries.ToArray();
            if (snapshot.Length > alreadySeen)
            {
                return snapshot[^1];
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("No new /api/issues request arrived after choosing a state.");
    }

    private static List<string> _Options(ComboBox comboBox) =>
        Dispatcher.UIThread.Invoke(() => comboBox.ItemsSource?.Cast<string>().ToList() ?? []);

    private static async Task<List<string>> _WaitForAsync(Func<List<string>> read, Func<List<string>, bool> isDone, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var value = read();
        while (!isDone(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            value = read();
        }

        return value;
    }

    private static Task _AnswerAsync(HttpContext context, ConcurrentQueue<string> issueQueries)
    {
        var path = context.Request.Path.Value;
        if (path == "/api/issues")
        {
            issueQueries.Enqueue(context.Request.QueryString.Value ?? string.Empty);
        }

        var body = path switch
        {
            "/api/admin/projects" => """[{"shortName":"AC","name":"AI Cockpit"}]""",
            "/api/admin/projects/AC/customFields" =>
                """[{"field":{"name":"State"},"bundle":{"values":[{"name":"Open"},{"name":"Ready"},{"name":"Test"}]}}]""",
            "/api/issues" =>
                """[{"id":"1-1","idReadable":"AC-1","summary":"One row","description":null,"project":{"shortName":"AC"},"customFields":[{"name":"State","$type":"StateIssueCustomField","value":{"name":"Open"}}]}]""",
            var other => throw new InvalidOperationException($"Unexpected request: {other}"),
        };

        return context.Response.WriteAsync(body);
    }
}

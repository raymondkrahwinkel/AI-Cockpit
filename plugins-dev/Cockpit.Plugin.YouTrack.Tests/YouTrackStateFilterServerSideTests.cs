using System.Collections.Concurrent;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.TestSupport;
using Microsoft.AspNetCore.Http;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// The dialog's state dropdown, sourced from the project's own status field (AC-518) rather than from whichever
/// issues happened to load — driven over a real HTTP round trip, the one path <see cref="YouTrackDialogControlTests"/>
/// deliberately avoids (its harness plants <c>_all</c> directly and gives every instance a blank token so the
/// dialog's own fetch short-circuits before it ever awaits anything real).
/// <para>
/// A real fetch cannot be driven inside a synchronous <see cref="HeadlessAvalonia.Run"/> body: the dialog kicks its
/// load off from an async-void event handler, and its continuation only runs once that body has returned control to
/// Avalonia's own dispatcher loop. So this test starts the dialog via <see cref="Dispatcher.UIThread.InvokeAsync"/>
/// (which does not block the loop) and then polls, from the test's own thread, until the fetch has settled.
/// </para>
/// </summary>
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
        Dispatcher.UIThread.Invoke(() => stateFilter!.SelectedItem = "Ready");

        var newQuery = await _WaitForQueryAsync(issueQueries, queriesBeforeChoosingState, TimeSpan.FromSeconds(5));

        // Uri.EscapeDataString turns the space in "Ready" into %20 and the colon/braces into their own escapes —
        // decode before asserting on the literal filter text rather than the wire form.
        var decoded = Uri.UnescapeDataString(newQuery);
        Assert.Contains("query=project:AC #Unresolved State: {Ready}", decoded, StringComparison.Ordinal);
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

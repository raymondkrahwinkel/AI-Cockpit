using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.LocalCi.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.LocalCi.UI;

// One checkout's workflow jobs: which of them can run on this machine, and the log of the one that is running
// (AC-1394: the UI part — LocalCiPlugin, the backend part, reads the workflows, runs the job and tracks it, and
// answers this view's questions over the plugin's channel). Opened for a session, so the checkout it shows is the
// one that session is working in.
// The log is redrawn on a timer rather than per line. A workflow job produces thousands of lines in bursts, and
// touching a text control on each one turns the run into a slideshow — the very cockpit-is-unusable problem the
// core limit exists to avoid, moved from the CPU to the UI thread.
internal sealed class LocalCiRunView : UserControl
{
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);

    private readonly ICockpitUiHost _host;
    private readonly string _projectRoot;

    private readonly StackPanel _jobs = new() { Spacing = 6 };
    private readonly TextBlock _headline = new() { TextWrapping = TextWrapping.Wrap };
    private readonly SelectableTextBlock _log = new() { FontFamily = new("Consolas, Menlo, monospace"), FontSize = 12 };
    private readonly ScrollViewer _logScroll;
    private readonly Button _stop;
    private readonly CheckBox _holdBackPullRequests;
    private readonly DispatcherTimer _redraw;

    private readonly Queue<string> _pending = new();
    private readonly LogTail _shown = new(maxLines: 400, maxCharacters: 120_000);
    private CancellationTokenSource? _inFlight;

    public LocalCiRunView(ICockpitUiHost host, string projectRoot)
    {
        _host = host;
        _projectRoot = projectRoot;

        _holdBackPullRequests = new CheckBox
        {
            Content = "Hold back pull requests from this checkout until a local run has passed",
            Margin = new(0, 8, 0, 0),
        };

        // AC-1033/AC-1041: the `?` beside the gate checkbox, pointing at what "a local run has passed" actually
        // checks (this exact commit, not just "recently") and where the bypass goes when it hasn't.
        var holdBackRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Children = { _holdBackPullRequests, host.CreateHelpHint("local-ci", "pull-request-gate") },
        };

        _stop = new Button { Content = "Stop", IsEnabled = false };
        _stop.Click += (_, _) => _Stop();

        _logScroll = new ScrollViewer { Content = _log, Height = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };

        _redraw = new DispatcherTimer { Interval = RedrawInterval };
        _redraw.Tick += (_, _) => _Flush();

        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = $"Workflow jobs in {projectRoot}. What runs here runs in a container on this machine — "
                        + "act's images are not GitHub's, so a pass here predicts the check on GitHub, it does not replace it.",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                },
                _jobs,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { _stop, _headline },
                },
                _logScroll,
                holdBackRow,
            },
        };

        _ = _ShowJobsAsync();
        _ = _ShowLastResultAsync();
        _ = _InitializeGateAsync();
    }

    private async Task _ShowJobsAsync()
    {
        _jobs.Children.Clear();

        IReadOnlyList<LocalCiWorkflowJobs> workflows;
        try
        {
            var payload = JsonSerializer.SerializeToElement(new LocalCiProjectRequest(_projectRoot), LocalCiChannel.Json);
            var answer = await _host.Channel.InvokeAsync(LocalCiChannel.Jobs, payload);
            workflows = answer.Deserialize<IReadOnlyList<LocalCiWorkflowJobs>>(LocalCiChannel.Json) ?? [];
        }
        catch (Exception exception)
        {
            _jobs.Children.Add(new TextBlock
            {
                Text = $"This project's workflows could not be read: {exception.Message}",
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var workflow in workflows)
        {
            if (workflow.Error is { } error)
            {
                _jobs.Children.Add(new TextBlock
                {
                    Text = $"{Path.GetFileName(workflow.WorkflowPath)} — {error}",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            foreach (var verdict in workflow.Jobs)
            {
                _jobs.Children.Add(_RowFor(workflow.WorkflowPath, verdict));
            }
        }

        if (_jobs.Children.Count == 0)
        {
            _jobs.Children.Add(new TextBlock { Text = "This project has no workflows to run.", Opacity = 0.7 });
        }
    }

    private Control _RowFor(string workflowPath, LocalCiJobVerdict verdict)
    {
        var name = new TextBlock
        {
            Text = $"{Path.GetFileName(workflowPath)} · {verdict.DisplayName}",
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 260,
        };

        if (!verdict.CanRunLocally)
        {
            return new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    name,
                    new TextBlock
                    {
                        Text = verdict.Reason,
                        Opacity = 0.7,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
        }

        var run = new Button { Content = "Run here" };
        run.Click += (_, _) => _ = _RunAsync(workflowPath, verdict.JobId);

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { name, run },
        };
    }

    private async Task _RunAsync(string workflowPath, string jobId)
    {
        if (_inFlight is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;
        _headline.Text = $"Running {jobId}…";
        _shown.Add($"$ {jobId} in {workflowPath}");

        _stop.IsEnabled = true;
        _redraw.Start();

        using var lines = _host.Channel.Subscribe(LocalCiChannel.RunLine, e => _OnRunLine(jobId, e));

        LocalCiRunResult? result = null;
        try
        {
            // No consent question here: the operator is the one asking, and a prompt in front of the button they
            // just pressed asks them to approve their own click.
            var payload = JsonSerializer.SerializeToElement(new LocalCiRunRequest(_projectRoot, workflowPath, jobId), LocalCiChannel.Json);
            var answer = await _host.Channel.InvokeAsync(LocalCiChannel.Run, payload, cancellation.Token);
            result = answer.Deserialize<LocalCiRunResult>(LocalCiChannel.Json);
        }
        finally
        {
            _redraw.Stop();
            _Flush();
            _inFlight = null;
            _stop.IsEnabled = false;
        }

        _headline.Text = result?.Headline ?? "The run ended without a verdict.";
    }

    // The window's own Stop. Tolerant of a token source already disposed: completing a run drops `_inFlight`, but
    // the operator can be pressing Stop at that exact moment.
    private void _Stop()
    {
        try
        {
            _inFlight?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished on its own between the click and here. Nothing left to stop.
        }
    }

    private async Task _ShowLastResultAsync()
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new LocalCiProjectRequest(_projectRoot), LocalCiChannel.Json);
            var answer = await _host.Channel.InvokeAsync(LocalCiChannel.LastRun, payload);
            var summary = answer.Deserialize<LocalCiRunSummary>(LocalCiChannel.Json);
            _headline.Text = summary?.Headline ?? "Nothing has been run here yet.";
        }
        catch (Exception exception)
        {
            _headline.Text = $"The last run could not be read: {exception.Message}";
        }
    }

    private async Task _InitializeGateAsync()
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new LocalCiProjectRequest(_projectRoot), LocalCiChannel.Json);
            var answer = await _host.Channel.InvokeAsync(LocalCiChannel.GateGet, payload);
            _holdBackPullRequests.IsChecked = answer.Deserialize<bool>(LocalCiChannel.Json);
        }
        catch (Exception)
        {
            // Best-effort: the checkbox stays unchecked, same as an off gate.
        }

        // Attached only after the fetch above, so setting the fetched value does not itself send a Set back.
        _holdBackPullRequests.IsCheckedChanged += (_, _) => _ = _SetGateAsync(_holdBackPullRequests.IsChecked ?? false);
    }

    private async Task _SetGateAsync(bool on)
    {
        try
        {
            var payload = JsonSerializer.SerializeToElement(new LocalCiGateSetRequest(_projectRoot, on), LocalCiChannel.Json);
            await _host.Channel.InvokeAsync(LocalCiChannel.GateSet, payload);
        }
        catch (Exception)
        {
            // Best-effort; a failed toggle leaves the backend's own setting as it was.
        }
    }

    // Called on the channel's publishing thread for every job's output, from every open dialog — filtered to this
    // run alone. The queue is what makes the cross-thread hand-off to the UI-thread redraw timer safe.
    private void _OnRunLine(string jobId, PluginChannelEvent channelEvent)
    {
        var line = channelEvent.Payload.Deserialize<LocalCiRunLine>(LocalCiChannel.Json);
        if (line is null
            || !string.Equals(line.ProjectRoot, _projectRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(line.JobId, jobId, StringComparison.Ordinal))
        {
            return;
        }

        lock (_pending)
        {
            _pending.Enqueue(line.Text);
        }
    }

    private void _Flush()
    {
        List<string> arrived;
        lock (_pending)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            arrived = [.. _pending];
            _pending.Clear();
        }

        foreach (var line in arrived)
        {
            _shown.Add(line);
        }

        _log.Text = _shown.Text();
        _logScroll.ScrollToEnd();
    }
}

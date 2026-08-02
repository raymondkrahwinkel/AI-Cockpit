using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Gate;
using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Ui;

// One checkout's workflow jobs: which of them can run on this machine, and the log of the one that is running.
// Opened for a session, so the checkout it shows is the one that session is working in.
// The log is redrawn on a timer rather than per line. A workflow job produces thousands of lines in bursts, and
// touching a text control on each one turns the run into a slideshow — the very cockpit-is-unusable problem the
// core limit exists to avoid, moved from the CPU to the UI thread.
internal sealed class LocalCiRunView : UserControl
{
    private static readonly TimeSpan RedrawInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILocalJobRunner _runner;
    private readonly LocalRunTracker _tracker;
    private readonly Func<string, CancellationToken, Task<string?>> _readHeadCommit;
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

    public LocalCiRunView(
        string projectRoot,
        ILocalJobRunner runner,
        LocalRunTracker tracker,
        Func<string, CancellationToken, Task<string?>> readHeadCommit,
        PullRequestGateSettings gate)
    {
        _projectRoot = projectRoot;
        _runner = runner;
        _tracker = tracker;
        _readHeadCommit = readHeadCommit;

        _holdBackPullRequests = new CheckBox
        {
            Content = "Hold back pull requests from this checkout until a local run has passed",
            IsChecked = gate.IsOnFor(projectRoot),
            Margin = new(0, 8, 0, 0),
        };
        _holdBackPullRequests.IsCheckedChanged += (_, _) =>
            gate.Set(projectRoot, _holdBackPullRequests.IsChecked ?? false);

        _stop = new Button { Content = "Stop", IsEnabled = false };
        _stop.Click += (_, _) =>
        {
            if (_inFlight is { } running)
            {
                _ = _StopAsync(running);
            }
        };

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
                _holdBackPullRequests,
            },
        };

        _ShowJobs();
        _ShowLastResult();
    }

    private void _ShowJobs()
    {
        _jobs.Children.Clear();

        foreach (var read in WorkflowCatalog.ReadProject(_projectRoot))
        {
            if (read.Document is not { } document)
            {
                _jobs.Children.Add(new TextBlock
                {
                    Text = $"{Path.GetFileName(read.Path)} — {read.Error}",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                });
                continue;
            }

            foreach (var verdict in LocalRunClassifier.Classify(document))
            {
                _jobs.Children.Add(_RowFor(read.Path, verdict));
            }
        }

        if (_jobs.Children.Count == 0)
        {
            _jobs.Children.Add(new TextBlock { Text = "This project has no workflows to run.", Opacity = 0.7 });
        }
    }

    private Control _RowFor(string workflowPath, JobVerdict verdict)
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
        run.Click += (_, _) => _ = _RunAsync(new LocalRunRequest(_projectRoot, workflowPath, verdict.JobId));

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { name, run },
        };
    }

    private async Task _RunAsync(LocalRunRequest request)
    {
        if (_inFlight is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _inFlight = cancellation;
        _headline.Text = $"Running {request.JobId}…";
        _shown.Add($"$ {request.JobId} in {request.WorkflowPath}");

        var startedAt = DateTimeOffset.UtcNow;
        var commit = await _readHeadCommit(_projectRoot, cancellation.Token);
        _tracker.Begin(_projectRoot, request.JobId, startedAt, () => _StopAsync(cancellation));

        // Only now: until the tracker knows about this run there is nothing for a stop to be recorded against, and
        // the awaits above hand control back to the window in between.
        _stop.IsEnabled = true;
        _redraw.Start();

        var result = LocalRunResult.DidNotRun(
            request.WorkflowPath, request.JobId, LocalRunOutcome.Cancelled, "the run ended without a verdict.");
        try
        {
            // No consent question here: the operator is the one asking, and a prompt in front of the button they
            // just pressed asks them to approve their own click.
            result = await _runner.RunAsync(request, _Queue, approve: null, cancellation.Token);
        }
        finally
        {
            _redraw.Stop();
            _Flush();
            _inFlight = null;
            _stop.IsEnabled = false;

            // Inside the finally, and before the token source is disposed: a run the tracker is never told the end
            // of stays in the status bar for the life of the app, offering a Kill for something that stopped long
            // ago. Completing here also drops the stop callback that holds this token source.
            _tracker.Complete(_projectRoot, result, commit, DateTimeOffset.UtcNow);
        }

        _headline.Text = result.Headline;
    }

    // The status bar's Kill, and the window's own Stop. Tolerant of a token source already disposed: completing a
    // run drops this callback, but the operator can be pressing Kill at that exact moment.
    private static Task _StopAsync(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run finished on its own between the click and here. Nothing left to stop.
        }

        return Task.CompletedTask;
    }

    private void _ShowLastResult() =>
        _headline.Text = _tracker.LastFor(_projectRoot) is { } record
            ? record.Result.Headline
            : "Nothing has been run here yet.";

    // Called from the runner's thread — the queue is what makes that safe.
    private void _Queue(string line)
    {
        lock (_pending)
        {
            _pending.Enqueue(line);
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

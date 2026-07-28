using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugin.LocalCi.Execution;
using Cockpit.Plugin.LocalCi.Workflows;

namespace Cockpit.Plugin.LocalCi.Ui;

/// <summary>
/// One checkout's workflow jobs: which of them can run on this machine, and the log of the one that is running.
/// Opened for a session, so the checkout it shows is the one that session is working in.
/// </summary>
/// <remarks>
/// The log is redrawn on a timer rather than per line. A workflow job produces thousands of lines in bursts, and
/// touching a text control on each one turns the run into a slideshow — the very cockpit-is-unusable problem the
/// core limit exists to avoid, moved from the CPU to the UI thread.
/// </remarks>
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
    private readonly DispatcherTimer _redraw;

    private readonly Queue<string> _pending = new();
    private readonly LogTail _shown = new(maxLines: 400, maxCharacters: 120_000);
    private CancellationTokenSource? _inFlight;

    public LocalCiRunView(
        string projectRoot,
        ILocalJobRunner runner,
        LocalRunTracker tracker,
        Func<string, CancellationToken, Task<string?>> readHeadCommit)
    {
        _projectRoot = projectRoot;
        _runner = runner;
        _tracker = tracker;
        _readHeadCommit = readHeadCommit;

        _stop = new Button { Content = "Stop", IsEnabled = false };
        _stop.Click += (_, _) => _inFlight?.Cancel();

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
        _stop.IsEnabled = true;
        _headline.Text = $"Running {request.JobId}…";
        _shown.Add($"$ {request.JobId} in {request.WorkflowPath}");

        var startedAt = DateTimeOffset.UtcNow;
        var commit = await _readHeadCommit(_projectRoot, cancellation.Token);
        _tracker.Begin(_projectRoot, request.JobId, startedAt, () =>
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        });
        _redraw.Start();

        LocalRunResult result;
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
        }

        _tracker.Complete(_projectRoot, result, commit, DateTimeOffset.UtcNow);
        _headline.Text = result.Headline;
    }

    private void _ShowLastResult() =>
        _headline.Text = _tracker.LastFor(_projectRoot) is { } record
            ? record.Result.Headline
            : "Nothing has been run here yet.";

    /// <summary>Called from the runner's thread — the queue is what makes that safe.</summary>
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

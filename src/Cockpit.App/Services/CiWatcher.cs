using System.Diagnostics;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Assistant;
using Cockpit.Core.Ci;
using Cockpit.Core.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Services;

// A checkout to look at: which session is on it, and where. Two sessions on one directory are one thing to check —
// the checks belong to the branch, not to whoever is looking at it.
public sealed record WatchedCheckout(string PaneId, string Title, string Directory);

// AC-634: asks gh every few minutes whether the branch a session is on has gone red, and says so when it has. No
// model in the loop — gh answers and `RedChecks` decides — so a tick that finds nothing costs a process and nothing
// else. Nothing is started in response: what to do about a red check is the operator's call, or the assistant's.
public sealed class CiWatcher(
    IAttentionNotifier notifier,
    IAgentMessageInbox inbox,
    INotificationSettingsStore settingsStore,
    ILogger<CiWatcher>? logger = null) : ISingletonService, IDisposable
{
    // Long enough that a run gets time to finish and short enough that you hear about it while you are still on the
    // branch that broke. A CI run measured in minutes is not worth asking about every thirty seconds.
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // Who the message is from. Not a pane and never will be — the cockpit itself noticed this, not a neighbour.
    private const string SenderPaneId = "cockpit-ci-watch";

    private readonly ILogger<CiWatcher> _logger = logger ?? NullLogger<CiWatcher>.Instance;

    // The red checks already reported, per checkout, so a branch that stays red stays quiet.
    private readonly Dictionary<string, IReadOnlySet<string>> _reported = new(StringComparer.OrdinalIgnoreCase);

    // The checkouts already reported ready (AC-645), so a pull request that sits green all afternoon is said once.
    private readonly HashSet<string> _reportedReady = new(StringComparer.OrdinalIgnoreCase);

    private DispatcherTimer? _timer;
    private bool _looking;
    private bool _disposed;

    // The checkouts to watch, asked fresh every tick: the live sessions and the directories they are working in. Set
    // by the cockpit, which owns the session list; nothing is watched until it is.
    public Func<IReadOnlyList<WatchedCheckout>>? Watching { get; set; }

    // Runs `gh pr checks` in a directory and hands back what it printed. Replaced by the tests, which have no
    // repository, no network and no wish for either.
    public Func<string, CancellationToken, Task<string>> Probe { get; set; } = _AskGhAsync;

    // AC-645: `gh pr view` for the merge itself. A second call because `gh pr checks --json` has no `reviewDecision`
    // or `mergeable` to fold into — it only knows check fields — and only ever run once the checks are already green.
    public Func<string, CancellationToken, Task<string>> MergeProbe { get; set; } = _AskGhMergeStateAsync;

    // Starts watching the clock. Idempotent, and built on the UI thread because that is where the session list is
    // read and where a DispatcherTimer has to be created to ever tick at all (AC-368).
    public void Start()
    {
        if (_timer is not null || _disposed)
        {
            return;
        }

        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += _OnTick;
        _timer.Start();

        _ = RunOnceAsync();
    }

    // One look at every watched checkout. Public because the tests drive it directly rather than waiting five
    // minutes for a timer — the same seam `ScheduledResumeCoordinator.RunDueAsync` opens.
    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        // A look that outlasts the interval must not have a second one started on top of it: two answers racing to
        // update what has been reported is how a failure is announced twice, or not at all.
        if (_looking || Watching is null)
        {
            return;
        }

        var settings = await settingsStore.LoadAsync(cancellationToken);
        if (!settings.NotifyOnCiFailure)
        {
            // Checked before anything is run, not before anything is delivered: the cost of this feature is the
            // processes it starts, and an operator who turned it off should not be paying it.
            return;
        }

        _looking = true;
        try
        {
            var checkouts = Watching()
                .Where(checkout => !string.IsNullOrWhiteSpace(checkout.Directory))
                .GroupBy(checkout => checkout.Directory, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            _Forget(checkouts);

            foreach (var checkout in checkouts)
            {
                await _LookAsync(checkout, cancellationToken);
            }
        }
        finally
        {
            _looking = false;
        }
    }

    private async Task _LookAsync(WatchedCheckout checkout, CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await Probe(checkout.Directory, cancellationToken);
        }
        catch (Exception exception)
        {
            // No gh, no login, no pull request, no network. None of that is news every five minutes, and nothing has
            // been remembered that did not happen — the next look tries again.
            _logger.LogDebug(exception, "Asking gh about {Directory} failed; the next look will try again.", checkout.Directory);
            return;
        }

        var checks = RedChecks.Parse(output);
        var alreadyReported = _reported.GetValueOrDefault(checkout.Directory, new HashSet<string>(StringComparer.Ordinal));
        var newlyRed = RedChecks.NewlyRed(checks, alreadyReported);

        _reported[checkout.Directory] = RedChecks.RedNames(checks);

        if (!RedChecks.AllGreen(checks))
        {
            // Back out of ready here rather than below, so a pull request that was reported ready, took a push and
            // went red is news again when it comes back green — the red branch returns before ever getting there.
            _reportedReady.Remove(checkout.Directory);
        }

        if (newlyRed.Count > 0)
        {
            await _ReportAsync(checkout, newlyRed, cancellationToken);
            return;
        }

        await _LookAtReadinessAsync(checkout, checks, cancellationToken);
    }

    // AC-645: the mirror of a red check — nothing failing, nothing pending, nothing blocking the merge, and nobody
    // pressing the button. Said once per crossing into ready, the same way red is said once per crossing into red.
    private async Task _LookAtReadinessAsync(WatchedCheckout checkout, IReadOnlyList<CiCheck> checks, CancellationToken cancellationToken)
    {
        if (!RedChecks.AllGreen(checks))
        {
            return;
        }

        if (_reportedReady.Contains(checkout.Directory))
        {
            // Already said. Returning here is also what keeps a pull request left sitting ready at one gh process
            // per tick rather than two — green but not yet mergeable is the one case that pays for the second.
            return;
        }

        string output;
        try
        {
            output = await MergeProbe(checkout.Directory, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Asking gh about the merge state of {Directory} failed; the next look will try again.", checkout.Directory);
            return;
        }

        if (!RedChecks.ParseMergeState(output).IsReadyToMerge)
        {
            return;
        }

        _reportedReady.Add(checkout.Directory);
        await _ReportReadyAsync(checkout, cancellationToken);
    }

    private async Task _ReportReadyAsync(WatchedCheckout checkout, CancellationToken cancellationToken)
    {
        _logger.LogInformation("CI went green and the pull request is mergeable on {Title} ({Directory}).", checkout.Title, checkout.Directory);

        await notifier.NotifyAttentionAsync(
            new AttentionNotification(checkout.Title, "CI is green and the pull request is ready to merge"),
            cancellationToken);

        inbox.Deliver(
            SenderPaneId,
            AssistantIdentity.PaneId,
            "ci",
            $"CI is green on '{checkout.Title}' ({checkout.Directory}) and the pull request is mergeable with nothing "
                + "blocking review. It is still unmerged. Nothing has been started about it.");
    }

    private async Task _ReportAsync(WatchedCheckout checkout, IReadOnlyList<CiCheck> red, CancellationToken cancellationToken)
    {
        var named = string.Join(", ", red.Select(check => check.Name));
        _logger.LogInformation("CI went red on {Title} ({Directory}): {Checks}.", checkout.Title, checkout.Directory, named);

        // The operator first, over the line they already tuned: an OS toast at the desk, Discord when away.
        await notifier.NotifyAttentionAsync(
            new AttentionNotification(checkout.Title, $"CI failed: {named}"),
            cancellationToken);

        // And the assistant, which is the one coordinating these pull requests (AC-632). It reads this on its next
        // turn or tool call, so a tick nobody needed to hear about costs it nothing.
        inbox.Deliver(
            SenderPaneId,
            AssistantIdentity.PaneId,
            "ci",
            $"CI failed on '{checkout.Title}' ({checkout.Directory}): {named}. "
                + $"{string.Join(" ", red.Select(check => check.Link).Where(link => link.Length > 0))} "
                + "Nothing has been started about it.");
    }

    // Drops what was remembered about checkouts nobody is on any more, so a long run does not accumulate the branches
    // of every session ever closed.
    private void _Forget(IReadOnlyList<WatchedCheckout> checkouts)
    {
        var live = checkouts.Select(checkout => checkout.Directory).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _reported.Keys.Where(directory => !live.Contains(directory)).ToList())
        {
            _reported.Remove(gone);
        }

        _reportedReady.RemoveWhere(directory => !live.Contains(directory));
    }

    private async void _OnTick(object? sender, EventArgs e)
    {
        try
        {
            await RunOnceAsync();
        }
        catch (Exception exception)
        {
            // A watcher must never be the reason the cockpit falls over, but it must leave a trace — a failure that
            // stops the loop silently is a watcher that reports green forever.
            _logger.LogError(exception, "A CI check failed; the next one will try again.");
        }
    }

    // `gh pr checks` for the pull request of whatever branch this directory is on. The exit code is ignored on
    // purpose: gh exits 8 while checks are pending and 1 when one failed, and writes the JSON either way.
    private static Task<string> _AskGhAsync(string workingDirectory, CancellationToken cancellationToken) =>
        _RunGhAsync(workingDirectory, ["pr", "checks", "--json", "bucket,name,workflow,link"], cancellationToken);

    // AC-645: the two fields that say whether anything is still blocking the merge. A merged or closed pull request
    // makes this fail or answer nothing, which reads as not ready — so a branch already merged is never reported.
    private static Task<string> _AskGhMergeStateAsync(string workingDirectory, CancellationToken cancellationToken) =>
        _RunGhAsync(workingDirectory, ["pr", "view", "--json", "reviewDecision,mergeable"], cancellationToken);

    private static async Task<string> _RunGhAsync(string workingDirectory, string[] arguments, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return string.Empty;
        }

        var startInfo = new ProcessStartInfo("gh")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
        }
        catch (Exception)
        {
            // gh is not installed or not on PATH. Nothing to watch with, and nothing worth saying about it.
            return string.Empty;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            // Both streams drained concurrently: a gh that fills the stderr pipe while nothing reads it never exits.
            var readOutput = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var readError = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            _ = await readError.ConfigureAwait(false);
            return await readOutput.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception)
            {
                // Already gone between the check and the kill.
            }

            return string.Empty;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Watching = null;

        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= _OnTick;
        _timer = null;
    }
}

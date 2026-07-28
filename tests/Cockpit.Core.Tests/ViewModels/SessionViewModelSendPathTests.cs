using System.Text.RegularExpressions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// A session pane hands a turn to its runtime in one place, so turn-start delivery (AC-394) cannot be reached by one
/// send path and missed by another.
/// <para>
/// This is written as a source scan for the same reason <c>ExternalLinkSingleSourceTests</c> and
/// <c>PluginVersionSingleSourceTests</c> are: what it guards against is written in C#, one call site at a time, and a
/// private method on a view model is not reachable by reflection. It is a tripwire rather than a proof — a send that
/// reached the runtime through a local variable, or through a different type entirely, would not be seen here.
/// </para>
/// <para>
/// The reason it is worth having: this pane already grew two send paths independently — the composer's, and the one a
/// scheduled resume uses — and AC-294 is on the board precisely because a feature landing on one route and not the
/// other has happened four times in this codebase. A third path added later would compile, pass every test in
/// <c>SessionViewModelInboxDeliveryTests</c>, and quietly deliver no mail.
/// </para>
/// </summary>
public partial class SessionViewModelSendPathTests
{
    [Fact]
    public void OnlyTheDeliveryFunnel_HandsATurnToTheRuntime()
    {
        var source = File.ReadAllText(_LocateSessionViewModel());

        // The funnel is still there and still the thing that sends. Without this the test would pass for the wrong
        // reason the moment the method was renamed away.
        Assert.Contains("_SendWithWaitingMessagesAsync", source, StringComparison.Ordinal);

        var callSites = RuntimeSendRegex().Matches(source).Count;

        Assert.True(
            callSites == 1,
            $"SessionViewModel calls the runtime's SendUserMessageAsync {callSites} times; it must do so exactly once, "
            + "inside _SendWithWaitingMessagesAsync. A second call site is a turn that carries no waiting mail — the "
            + "agent it was addressed to never sees it, and the sender was told it arrived. Route the new path through "
            + "the funnel instead of sending directly.");
    }

    /// <summary>Whitespace-tolerant so reformatting the call does not quietly retire this test.</summary>
    [GeneratedRegex(@"runtime\s*\.\s*SendUserMessageAsync\s*\(")]
    private static partial Regex RuntimeSendRegex();

    private static string _LocateSessionViewModel()
    {
        var relative = Path.Combine("src", "Cockpit.App", "ViewModels", "SessionViewModel.cs");
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException($"No {relative} above the test output — this test reads the repo it belongs to.");
    }
}

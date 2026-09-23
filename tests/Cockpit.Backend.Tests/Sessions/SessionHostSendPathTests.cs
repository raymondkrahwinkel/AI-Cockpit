using System.Text.RegularExpressions;

namespace Cockpit.Backend.Tests.Sessions;

/// <summary>
/// Moved from <c>SessionViewModelSendPathTests</c> with the send itself (AC-1376): a turn reaches the runtime in one
/// place, so turn-start delivery (AC-394) cannot be reached by one send path and missed by another. A source scan
/// and a tripwire rather than a proof, like <c>ExternalLinkSingleSourceTests</c>: a private method is not reachable by
/// reflection, and a third send path added later would compile, pass every inbox test, and quietly deliver no mail.
/// </summary>
public partial class SessionHostSendPathTests
{
    [Theory]
    [InlineData("src/Cockpit.Infrastructure/Sessions/SessionHost.cs", 1)]
    [InlineData("src/Cockpit.App/ViewModels/SessionViewModel.cs", 0)]
    public void OnlyTheHostsDeliveryFunnel_HandsATurnToTheRuntime(string file, int expectedCallSites)
    {
        // The funnel is still there and still the thing that sends. Without this the test would pass for the wrong
        // reason the moment the method was renamed away.
        Assert.Contains("_SendWithWaitingMessagesAsync", File.ReadAllText(_Locate("src/Cockpit.Infrastructure/Sessions/SessionHost.cs")), StringComparison.Ordinal);

        var callSites = RuntimeSendRegex().Matches(File.ReadAllText(_Locate(file))).Count;

        Assert.True(
            callSites == expectedCallSites,
            $"{file} calls the runtime's SendUserMessageAsync {callSites} times; expected {expectedCallSites}. The one "
            + "call belongs inside SessionHost._SendWithWaitingMessagesAsync. A second call site is a turn that carries no "
            + "waiting mail — the agent it was addressed to never sees it, and the sender was told it arrived. Route the "
            + "new path through the funnel instead of sending directly.");
    }

    /// <summary>
    /// Whitespace- and case-tolerant, so reformatting the call or reaching the runtime through the view model's own
    /// <c>_Runtime</c> property does not slip past it, and tolerant of <c>?.</c> and <c>!.</c> because those are the
    /// shapes a bypass would most plausibly take.
    /// </summary>
    [GeneratedRegex(@"runtime\s*[?!]?\s*\.\s*SendUserMessageAsync\s*\(", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimeSendRegex();

    private static string _Locate(string relative)
    {
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

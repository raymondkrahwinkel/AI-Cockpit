using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// The fail-closed confinement gate on an embedded start (AC-174, AC-191). An embedded run that asked for its file
/// tools to stay inside its working directory must not start on a provider that does not keep them there — on
/// <em>either</em> route that asks for it: an isolated run with its own worktree, and a run confined to the folder as
/// given without one (the non-git Autopilot path, and the CEO validator reading a run's accumulated work).
///
/// Pinned because the second route was the gap: the confine flag was set and passed to the driver, but only the
/// isolate route was checked — a caller asked for confinement, got a "yes", and a provider that ignores the flag would
/// have run an autonomous, prompt-injectable agent with the operator's whole filesystem in reach.
/// Provider-neutral: nothing here names a brand; the gate reads the capability a provider vouches.
/// </summary>
public class EmbeddedConfinementGateTests
{
    private const string Profile = "work";
    private const string Folder = "/home/raymond/run";

    private const bool Ready = true;
    private const bool Confines = true;

    // The confine-without-a-worktree route as its callers build it: a folder to be held to, and no worktree.
    private static EmbeddedSessionRequest _Confined() =>
        new() { ConfineFileToolsToWorkingDirectory = true, WorkingDirectory = Folder };

    private static EmbeddedSessionRequest _Isolated() =>
        new() { IsolateInWorktree = true, WorkingDirectory = Folder };

    private static string? _Refusal(EmbeddedSessionRequest request, bool isSessionReady = Ready, bool confinesFileAccess = Confines) =>
        CockpitViewModel._EmbeddedConfinementRefusal(request, Profile, isSessionReady, confinesFileAccess);

    [Fact]
    public void ARunThatAskedForNeither_IsNotGated()
    {
        // An ordinary embedded session (the CEO planning round) asks for no confinement, so a non-confining provider is
        // its operator's own choice to make — the gate must not turn every plain embed into a refusal.
        Assert.Null(_Refusal(new EmbeddedSessionRequest(), isSessionReady: Ready, confinesFileAccess: false));
    }

    [Fact]
    public void AnIsolatedRun_OnAConfiningProvider_Proceeds()
    {
        Assert.Null(_Refusal(_Isolated()));
    }

    [Fact]
    public void AConfinedRunWithoutAWorktree_OnAConfiningProvider_Proceeds()
    {
        Assert.Null(_Refusal(_Confined()));
    }

    [Fact]
    public void AnIsolatedRun_OnANonConfiningProvider_IsRefused_NamingTheProfileAndTheWayOut()
    {
        var refusal = _Refusal(_Isolated(), confinesFileAccess: false);

        Assert.NotNull(refusal);
        // The operator has to be able to act on this: which profile refused, and what to do about it. The way out names
        // both routes a bypass mode arrives by — a step's autonomy mode and the profile a CEO runs on — because a
        // refusal that names only one sends half of them looking in the wrong place.
        Assert.Contains(Profile, refusal);
        Assert.Contains("bypassPermissions", refusal);
        Assert.Contains("the profile, or the Autopilot autonomy mode", refusal);
        // An isolated run is refused in terms of the worktree it would have had, and the checkout it would have hit.
        Assert.Contains("to the worktree", refusal);
        Assert.Contains("edit your real checkout", refusal);
    }

    [Fact]
    public void AConfinedRunWithoutAWorktree_OnANonConfiningProvider_IsRefused()
    {
        // The AC-191 gap. The non-git Autopilot path sets ConfineFileToolsToWorkingDirectory without IsolateInWorktree;
        // a provider that neither confines natively nor vouches the capability must be refused here, exactly as the
        // isolate route is — otherwise the flag is a promise nobody checks.
        var refusal = _Refusal(_Confined(), confinesFileAccess: false);

        Assert.NotNull(refusal);
        Assert.Contains(Profile, refusal);
        // No worktree exists on this path, so the refusal talks about the folder the run was pointed at instead — it
        // must not tell the operator about a worktree and a checkout that were never part of what they set up.
        Assert.Contains("outside the folder it was given", refusal);
        Assert.DoesNotContain("real checkout", refusal);
        Assert.DoesNotContain("worktree", refusal);
    }

    [Fact]
    public void AConfinedRunWithNoWorkingDirectory_IsRefused_EvenOnAConfiningProvider()
    {
        // Confinement to nothing is not confinement: an empty directory starts the session wherever the cockpit itself
        // is, and a natively confining provider would vouch honestly for that folder — so the capability check alone
        // would wave through a run roaming the operator's disk. Refused before the capability is even consulted.
        var refusal = _Refusal(new EmbeddedSessionRequest { ConfineFileToolsToWorkingDirectory = true });

        Assert.NotNull(refusal);
        Assert.Contains("was given none", refusal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ARunWhoseSessionNeverStarted_IsRefused_WithoutReadingItsCapabilities(bool isolate, bool confine)
    {
        // A failed start leaves Capabilities at their pre-start default, whose confines flag is true — so readiness has
        // to be checked first or a stale "confined" reading becomes licence to run. Every route, all fail closed.
        var request = new EmbeddedSessionRequest
        {
            IsolateInWorktree = isolate,
            ConfineFileToolsToWorkingDirectory = confine,
            WorkingDirectory = Folder,
        };

        var refusal = _Refusal(request, isSessionReady: false, confinesFileAccess: Confines);

        Assert.NotNull(refusal);
        Assert.Contains("its session did not start", refusal);
    }

    [Fact]
    public void ARunThatBothIsolatesAndConfines_IsRefusedInTermsOfItsWorktree()
    {
        // Both flags together is what an isolate-in-worktree run reaching a driver looks like from the options map's
        // side. The wording has to settle on one story, and the worktree is the one the operator set up — pinned so a
        // later edit to the tuple cannot quietly start telling them about a folder instead.
        var request = new EmbeddedSessionRequest
        {
            IsolateInWorktree = true,
            ConfineFileToolsToWorkingDirectory = true,
            WorkingDirectory = Folder,
        };

        Assert.Contains("to the worktree", _Refusal(request, confinesFileAccess: false));
    }
}

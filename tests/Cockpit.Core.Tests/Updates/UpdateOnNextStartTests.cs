using Cockpit.Core.Updates;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The operator's "install on next start" as it survives between two launches (AC-738), against a state root of its
/// own rather than the one this machine's cockpit is using.
/// </summary>
public class UpdateOnNextStartTests : IDisposable
{
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "cockpit-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, recursive: true);
        }
    }

    [Fact]
    public void ALaunchWithNoRequest_AppliesNothing()
    {
        Assert.False(UpdateOnNextStart.TakeRequest(_stateRoot));
    }

    [Fact]
    public void ARequestedUpdate_IsAppliedByTheNextLaunch()
    {
        Assert.True(UpdateOnNextStart.Request(_stateRoot));

        Assert.True(UpdateOnNextStart.TakeRequest(_stateRoot));
    }

    /// <summary>
    /// The request is taken, not read: an update pass re-runs the same <c>Main</c>, and a package that cannot be
    /// applied would otherwise have every launch after this one try it again.
    /// </summary>
    [Fact]
    public void ARequestAlreadyTaken_DoesNotApplyASecondTime()
    {
        UpdateOnNextStart.Request(_stateRoot);
        UpdateOnNextStart.TakeRequest(_stateRoot);

        Assert.False(UpdateOnNextStart.TakeRequest(_stateRoot));
    }

    /// <summary>
    /// A first run has no state directory yet — the request has to create it, because the answer "we could not write
    /// it down" is what the operator is told instead of the promise that it will install.
    /// </summary>
    [Fact]
    public void ARequestOnAMachineWithNoStateDirectory_CreatesItRatherThanFailing()
    {
        Assert.False(Directory.Exists(_stateRoot));

        Assert.True(UpdateOnNextStart.Request(_stateRoot));
    }

    /// <summary>
    /// A state root that cannot be created is reported rather than thrown: this runs from a click, and the caller
    /// turns the false into a message instead of taking the cockpit down with it.
    /// </summary>
    [Fact]
    public void ARequestThatCannotBeWritten_IsReportedRatherThanThrown()
    {
        var file = Path.Combine(_stateRoot, "occupied");
        Directory.CreateDirectory(_stateRoot);
        File.WriteAllText(file, string.Empty);

        Assert.False(UpdateOnNextStart.Request(Path.Combine(file, "state")));
    }
}

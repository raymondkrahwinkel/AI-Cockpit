namespace Cockpit.Plugins.Abstractions.HeavyRuns;

// AC-1535: what a host without a queue hands out — the run may start at once, and nothing is ever revoked.
public sealed class AdmittedHeavyRunSlot : IHeavyRunSlot
{
    public static readonly AdmittedHeavyRunSlot Instance = new();

    private AdmittedHeavyRunSlot()
    {
    }

    public Task Admitted => Task.CompletedTask;

    public string? WaitReason => null;

    public CancellationToken Revoked => CancellationToken.None;

    public Task ReportFailureAsync(string output) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

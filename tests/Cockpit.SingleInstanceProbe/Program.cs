using Cockpit.Infrastructure.Configuration;
using Cockpit.Core.Updates;

namespace Cockpit.SingleInstanceProbe;

// AC-1221: a cockpit reduced to the one thing under test — it derives the claim for the state root it was
// pointed at, asks for it, and reports the answer as an exit code. COCKPIT_STATE_ROOT is the whole input, so
// this goes through the same public path a real launch does and nothing here decides the name itself.
internal static class Program
{
    // Distinct from the runtime's own failure codes, so a probe that crashed cannot be read as one that stood down.
    private const int ClaimTaken = 0;
    private const int ClaimRefused = 3;
    private const int StagedUpdateDeferred = 4;

    private static int Main(string[] args)
    {
        if (args.Contains("--stage-update"))
        {
            return SingleInstanceGuard.IsHeldByAnotherCockpit() || !UpdateOnNextStart.TakeRequest()
                ? StagedUpdateDeferred
                : ClaimTaken;
        }

        using var guard = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false);

        return guard is null ? ClaimRefused : ClaimTaken;
    }
}

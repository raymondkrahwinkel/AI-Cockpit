using System.Diagnostics;
using Cockpit.Core.Configuration;
using Cockpit.Infrastructure.Configuration;
using Cockpit.TestSupport;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// AC-1221: two cockpits over one state directory have to derive the same claim name, and that is the one
/// property <see cref="SingleInstanceGuardTests"/> cannot reach. All eighteen of its tests derive the name
/// inside a single process, where a per-process seed agrees with itself as reliably as SHA-256 does — swapping
/// the fingerprint for <see cref="string.GetHashCode()"/> leaves every one of them green while two real
/// cockpits stop seeing each other and write over one state root (the AC-4 corruption).
/// </summary>
/// <remarks>
/// The second cockpit is a console probe rather than the app: the instance that loses the claim raises an
/// Avalonia notice instead of exiting, so running the app here would leave windows hanging. That is why this
/// check was manual until now, and a manual check never runs.
/// </remarks>
public sealed class SingleInstanceClaimAcrossProcessesTests
{
    private const int ClaimTaken = 0;
    private const int ClaimRefused = 3;

    // The probe is a build dependency of this project, so it was built in this configuration as well. Picking the
    // matching one keeps a leftover build of the other configuration — carrying its own older copy of
    // Cockpit.Infrastructure, and so its own older fingerprint — from being what this test actually measures.
    private const string BuildConfiguration =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    [Fact]
    public void TryAcquire_InASecondProcessOnAStateRootThisOneHolds_Refuses()
    {
        var probe = _LocateProbe();
        var held = Directory.CreateTempSubdirectory("cockpit-claim-held");
        var unrelated = Directory.CreateTempSubdirectory("cockpit-claim-unrelated");

        try
        {
            // Gate: this root takes the fingerprinted branch. A derivation that collapsed to the shared default
            // name would refuse the probe for a reason that has nothing to do with the fingerprint, and the
            // assertion at the end would hold with no fingerprint left in the code at all.
            var heldClaim = SingleInstanceGuard.ClaimNameFor(held.FullName);
            Assert.NotEqual(SingleInstanceGuard.ClaimNameFor(CockpitBuild.DefaultStateRoot), heldClaim);

            using var thisCockpit = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, heldClaim);
            Assert.NotNull(thisCockpit);

            using var elsewhere = _RunProbe(probe, unrelated.FullName);

            // Gate: a probe taking a claim is an outcome this setup can see. That is precisely the symptom of a
            // per-process fingerprint, so without this the assertion below could be one no run is able to falsify.
            Assert.Equal(ClaimTaken, elsewhere.ExitCode);

            // Gate: the probe really is a process of its own. Derived twice in this one, any fingerprint agrees
            // with itself — which is the whole reason the existing eighteen tests cannot see this.
            Assert.NotEqual(Environment.ProcessId, elsewhere.Id);

            using var second = _RunProbe(probe, held.FullName);

            Assert.NotEqual(Environment.ProcessId, second.Id);
            Assert.Equal(ClaimRefused, second.ExitCode);
        }
        finally
        {
            held.Delete(recursive: true);
            unrelated.Delete(recursive: true);
        }
    }

    /// <summary>A second cockpit, pointed at <paramref name="stateRoot"/> and run to completion.</summary>
    private static Process _RunProbe(string probe, string stateRoot)
    {
        var startInfo = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(probe);

        // The only input the probe gets: it resolves the state root and the claim itself, through the same public
        // path a real launch takes. Handing it a claim name would move the derivation back into this process.
        startInfo.Environment[CockpitBuild.StateRootVariable] = stateRoot;

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException("dotnet exec started no process");

        Assert.True(process.WaitForExit(30_000), $"the probe on {stateRoot} did not exit in time");

        return process;
    }

    private static string _LocateProbe()
    {
        var output = Path.Combine(RepositoryPaths.Root, "tests", "Cockpit.SingleInstanceProbe", "bin");
        var configuration = $"{Path.DirectorySeparatorChar}{BuildConfiguration}{Path.DirectorySeparatorChar}";

        return Directory
                   .EnumerateFiles(output, "Cockpit.SingleInstanceProbe.dll", SearchOption.AllDirectories)
                   .FirstOrDefault(path => path.Contains(configuration, StringComparison.Ordinal))
               ?? throw new InvalidOperationException(
                   $"no {BuildConfiguration} build of the probe under {output} — it is a build dependency of this test project");
    }
}

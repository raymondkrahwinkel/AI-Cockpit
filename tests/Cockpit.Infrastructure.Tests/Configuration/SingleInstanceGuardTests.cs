using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Tests.Configuration;

/// <summary>
/// Two cockpits over one state directory write over each other's settings, and the second one's startup deletes
/// the mcp-config and plugin files the first one's sessions are still reading (AC-4). These tests hold the claim
/// that stops the second one — and the exemption that lets a development build run beside it anyway (AC-3).
/// </summary>
/// <remarks>
/// <para>
/// The claim is held on a thread of its own, because a mutex is owned by a thread and is re-entrant to it: asking
/// for it twice from the test's own thread is granted both times, which measures nothing. A separate owner is the
/// nearest thing to the second cockpit this suite can produce in one process.
/// </para>
/// <para>
/// What that leaves unproven is the reach of the claim across processes and — on Unix — across shells, which is
/// the .NET/OS guarantee the options below buy rather than anything this code decides. It was measured by hand on
/// Windows (two processes: taken, refused, and no stale claim after a hard kill); on Fedora it is still the open
/// question in <c>Memory/Cockpit/Todo.md</c>.
/// </para>
/// <para>
/// Every test claims a name of its own. The real name is system-wide by design, so a test using it would answer
/// to whether a cockpit happens to be open on this machine: red on Raymond's desktop while he is using the app,
/// green on a runner, for reasons that have nothing to do with the code.
/// </para>
/// </remarks>
public sealed class SingleInstanceGuardTests
{
    private static string UniqueClaimName() => $"AI-Cockpit-test-{Guid.NewGuid():N}";

    [Fact]
    public void TryAcquire_WhileAnotherCockpitHoldsTheClaim_Refuses()
    {
        var claimName = UniqueClaimName();
        using var other = new CockpitHoldingTheClaim(claimName);

        var second = SingleInstanceGuard.TryAcquire(isDevelopmentBuild: false, claimName);

        Assert.Null(second);
    }

    [Fact]
    public void ClaimNameFor_TwoDifferentRoots_AreDifferentClaims()
    {
        Assert.NotEqual(
            SingleInstanceGuard.ClaimNameFor(Path.Combine(Path.GetTempPath(), "cockpit-a")),
            SingleInstanceGuard.ClaimNameFor(Path.Combine(Path.GetTempPath(), "cockpit-b")));
    }

    [Fact]
    public void IsAnotherInstanceRunning_WhileAnotherInstanceHoldsItsLock_RefusesTheUpdate()
    {
        var installationDirectory = Directory.CreateTempSubdirectory("ac1486-");
        var instancesDirectory = Directory.CreateDirectory(Path.Combine(installationDirectory.FullName, ".instances"));
        var claimPath = Path.Combine(instancesDirectory.FullName, "other.lock");

        try
        {
            bool anotherInstanceRunning;
            using (var other = new FileStream(claimPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                anotherInstanceRunning = InstallationInstanceGuard.IsAnotherInstanceRunning(installationDirectory.FullName);
            }

            Assert.True(anotherInstanceRunning);
        }
        finally
        {
            Directory.Delete(installationDirectory.FullName, recursive: true);
        }
    }

    [Fact]
    public void IsAnotherInstanceRunning_WithAStaleLockOrUnavailableInstallation_CleansItOrAllowsStartup()
    {
        var installationDirectory = Directory.CreateTempSubdirectory("ac1486-");
        var instancesDirectory = Directory.CreateDirectory(Path.Combine(installationDirectory.FullName, ".instances"));
        var claimPath = Path.Combine(instancesDirectory.FullName, "stale.lock");
        var unavailableInstallationPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(claimPath, string.Empty);
            using var noOp = InstallationInstanceGuard.Acquire(unavailableInstallationPath);

            Assert.NotNull(noOp);
            Assert.False(InstallationInstanceGuard.IsAnotherInstanceRunning(installationDirectory.FullName));
            Assert.False(File.Exists(claimPath));
        }
        finally
        {
            File.Delete(unavailableInstallationPath);
            Directory.Delete(installationDirectory.FullName, recursive: true);
        }
    }

    /// <summary>Another cockpit, started and left open on a thread of its own, until disposed.</summary>
    private sealed class CockpitHoldingTheClaim : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public CockpitHoldingTheClaim(string claimName, bool isDevelopmentBuild = false)
        {
            var taken = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                using var guard = SingleInstanceGuard.TryAcquire(isDevelopmentBuild, claimName);
                taken.Set();
                _release.Wait();
            })
            {
                IsBackground = true,
            };

            _thread.Start();
            taken.Wait();
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join();
        }
    }
}

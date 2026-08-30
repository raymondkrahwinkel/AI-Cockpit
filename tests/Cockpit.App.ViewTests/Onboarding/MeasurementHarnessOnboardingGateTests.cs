#if DEBUG
using Cockpit.Core.Configuration;
using Cockpit.TestSupport;

namespace Cockpit.App.ViewTests.Onboarding;

public class MeasurementHarnessOnboardingGateTests
{
    [Fact]
    public void MeasurementHarnessBypass_StaysDebugOnlyAndRequiresAnIsolatedStateRoot()
    {
        var source = File.ReadAllText(Path.Combine(
            RepositoryPaths.Root, "src", "Cockpit.App", "App.axaml.cs"));

        // Source assertions are the only way a Debug-built test can guard the Release compile-time boundary.
        Assert.Matches(@"(?s)#if DEBUG\s+if \(_CanBypassOnboardingForMeasurementHarness\(.*?#endif", source);
        Assert.Matches(@"(?s)#if DEBUG\s+// A measurement needs.*?private static bool _CanBypassOnboardingForMeasurementHarness.*?#endif", source);

        var gate = typeof(Cockpit.App.App).GetMethod("_CanBypassOnboardingForMeasurementHarness",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(gate);

        Assert.False((bool)gate!.Invoke(null, [null, null])!);
        Assert.IsType<InvalidOperationException>(Assert.Throws<System.Reflection.TargetInvocationException>(
            () => gate.Invoke(null, ["1", null])).InnerException);
        Assert.IsType<InvalidOperationException>(Assert.Throws<System.Reflection.TargetInvocationException>(
            () => gate.Invoke(null, ["1", CockpitBuild.DefaultStateRoot])).InnerException);
        Assert.True((bool)gate.Invoke(null, ["1", Path.Combine(Path.GetTempPath(), "ac1249")])!);
    }
}
#endif

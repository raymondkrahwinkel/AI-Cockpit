using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;
using Velopack;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The two halves of AC-385: the hook that has to run before anything else, and the reading of whether this copy is
/// one the updater installed.
/// </summary>
public class VelopackBootstrapTests
{
    private const string TheHook = "VelopackApp.Build().SetAutoApplyOnStartup(_AppliesAStagedUpdate(args)).Run();";

    /// <summary>
    /// Installing, updating and uninstalling all re-run the executable with arguments Velopack handles, and the
    /// handler exits the process. Whatever sits above it in <c>Main</c> therefore runs on every one of those passes,
    /// in a window nobody sees — and what currently sits at the top of this <c>Main</c> is a single-instance lock, a
    /// credential-file sweep and a bundled-plugin install, none of which should happen during an update.
    /// <para>
    /// Read from the source rather than asserted at runtime: the property being pinned is <em>position</em>, and by
    /// the time the assembly is loaded there is nothing left that says which statement came first. This is the same
    /// source-tree-lint shape as <c>ThemeHexColorGuardTests</c>, and it exists because the natural way for this to
    /// break is somebody adding a line above the hook without ever thinking about update passes.
    /// </para>
    /// </summary>
    [Fact]
    public void TheVelopackHook_IsTheFirstStatementInMain()
    {
        Assert.Equal(TheHook, _FirstStatementInMain());
    }

    /// <summary>
    /// The first line of <c>Main</c> that is not blank and not a comment. Read from the source because the property
    /// being pinned is <em>position</em>, and by the time the assembly is loaded nothing says which statement came
    /// first.
    /// </summary>
    private static string? _FirstStatementInMain()
    {
        var lines = File.ReadAllLines(_ProgramPath());

        var main = Array.FindIndex(lines, line => line.Contains("public static void Main(", StringComparison.Ordinal));
        Assert.True(main >= 0, "Program.cs no longer declares a 'public static void Main(' — this test reads that method's body");

        var opening = Array.FindIndex(lines, main, line => line.Trim() == "{");
        Assert.True(opening > main, "no opening brace found after Main's signature");

        return lines.Skip(opening + 1)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal));
    }

    /// <summary>
    /// Velopack's locator is a process-wide singleton that the bootstrap in <c>Main</c> installs, and a test host
    /// never runs that bootstrap — so this covers the "no locator at all" state, not the "locator says this is not
    /// an installation" one. Both answer <see cref="UpdateSupport.NotPackaged"/>; only the first is reachable from
    /// here, and saying which is which is the point, because the version of this that reached the state through a
    /// caught exception looked identical and proved something else entirely.
    /// </summary>
    [Fact]
    public void TheProbe_WithoutABootstrappedLocator_ReportsNotPackaged()
    {
        var support = new VelopackUpdateSupportProbe().Detect();

        Assert.Equal(UpdateSupport.NotPackaged, support);
    }

    /// <summary>
    /// The same state, asserted one level down, because at the level above it is invisible: with or without the
    /// guard the answer is "not packaged", the difference being only whether it was reached through an exception the
    /// <c>catch</c> then swallowed. Asked here, the guard is the thing that decides whether this returns or throws —
    /// <c>VelopackLocator.Current</c> throws outright when no bootstrap ever set it.
    /// </summary>
    [Fact]
    public void TheReading_WithoutABootstrappedLocator_AnswersRatherThanThrows()
    {
        Assert.False(VelopackUpdateSupportProbe.IsInstalledCopy());
    }

    /// <summary>
    /// Velopack applies a staged update during <c>Run()</c> and restarts, unless told not to. This project does not
    /// do that on its own — applying is an action the operator takes — so the decision is the request they left
    /// behind, never a constant <c>true</c>.
    /// </summary>
    [Fact]
    public void TheVelopackHook_AppliesAnUpdateOnlyWhenOneWasAskedFor()
    {
        Assert.Contains("SetAutoApplyOnStartup(_AppliesAStagedUpdate(args))", _FirstStatementInMain(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Three launches must never apply a staged package: the two headless children, which would update-and-exit
    /// instead of doing the measurement they were spawned for, and a second cockpit that is about to stand down —
    /// applying force-stops every process in the installation directory, the running cockpit with it. All three are
    /// ruled out <em>before</em> the request is taken, so a launch that cannot use it leaves it behind.
    /// </summary>
    /// <remarks>
    /// Source-read for the same reason as the hook above: what is pinned is order, and a loaded assembly no longer
    /// says which check came first.
    /// </remarks>
    [Theory]
    [InlineData("HeadlessCalibration.IsRequested(args)")]
    [InlineData("HeadlessDictation.IsRequested(args)")]
    [InlineData("SingleInstanceGuard.IsHeldByAnotherCockpit()")]
    public void TheStagedUpdateDecision_RulesOutALaunchThatCannotApply_BeforeTakingTheRequest(string check)
    {
        var source = File.ReadAllText(_ProgramPath());

        var decision = source.IndexOf("private static bool _AppliesAStagedUpdate", StringComparison.Ordinal);
        Assert.True(decision >= 0, "Program.cs no longer has _AppliesAStagedUpdate — this test reads that method");

        var body = source[decision..];
        var take = body.IndexOf("UpdateOnNextStart.TakeRequest()", StringComparison.Ordinal);
        Assert.True(take >= 0, "_AppliesAStagedUpdate no longer takes the request");

        Assert.Contains(check, body[..take], StringComparison.Ordinal);
    }

    /// <summary>
    /// The rule that decides it, with both readings handed in — the only place it can be asked, because Velopack's
    /// locator is a process-wide singleton a test has no public way to stand up. Written as a table because the
    /// interesting case is the one the short circuit hides: a locator that exists and reports no installed version.
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void TheReading_IsInstalledOnlyWithALocatorThatNamesAVersion(bool locatorIsSet, bool hasVersion, bool expected)
    {
        var version = hasVersion ? new SemanticVersion(1, 2, 3) : null;

        Assert.Equal(expected, VelopackUpdateSupportProbe.IsInstalledCopy(locatorIsSet, () => version));
    }

    /// <summary>
    /// Without a locator the version is never asked for — reading it is exactly what throws, so the order of the
    /// two halves is the guard rather than a tidiness.
    /// </summary>
    [Fact]
    public void TheReading_WithoutALocator_DoesNotReachForTheVersion()
    {
        Assert.False(VelopackUpdateSupportProbe.IsInstalledCopy(
            locatorIsSet: false,
            static () => throw new InvalidOperationException("the version was read without a locator")));
    }

    [Fact]
    public void TheProbe_WhenTheCopyIsInstalled_ReportsSupported()
    {
        Assert.Equal(UpdateSupport.Supported, VelopackUpdateSupportProbe.Detect(static () => true));
    }

    [Fact]
    public void TheProbe_WhenTheCopyIsNotInstalled_ReportsNotPackaged()
    {
        Assert.Equal(UpdateSupport.NotPackaged, VelopackUpdateSupportProbe.Detect(static () => false));
    }

    /// <summary>
    /// The reading of the environment failing outright is still an answer, not an exception. This is the assertion
    /// the <c>catch</c> exists for, and removing that <c>catch</c> is what turns this red — the reading cannot be
    /// made to fail from outside, so it is handed in.
    /// </summary>
    [Fact]
    public void TheProbe_WhenTheReadingFails_ReportsNotPackagedRatherThanThrowing()
    {
        var support = VelopackUpdateSupportProbe.Detect(
            static () => throw new InvalidOperationException("the locator could not work out what this copy is"));

        Assert.Equal(UpdateSupport.NotPackaged, support);
    }

    /// <summary>
    /// The probe reaches the view model through an optional constructor parameter — the shape that compiles, runs,
    /// and quietly stays null, which is the very thing <c>UpdateDependencyInjectionTests</c> was written for. Here
    /// that shape is worse than usual: a probe that never arrives reads as "not packaged", which is a plausible
    /// answer, so nothing would look wrong. The container is therefore built the way <c>Program.cs</c> builds it,
    /// and asked.
    /// </summary>
    [Fact]
    public void TheContainer_HasAProbe_SoTheAnswerIsMeasuredRatherThanAssumed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCore().AddInfrastructure().AddServices(
            typeof(Core.DependencyInjection).Assembly,
            typeof(Infrastructure.DependencyInjection).Assembly,
            typeof(CockpitViewModel).Assembly);

        using var provider = services.BuildServiceProvider();

        Assert.IsType<VelopackUpdateSupportProbe>(provider.GetRequiredService<IUpdateSupportProbe>());
    }

    private static string _ProgramPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Cockpit.App", "Program.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "No folder above the test output holds src/Cockpit.App/Program.cs — this test reads the repo it belongs to.");
    }
}

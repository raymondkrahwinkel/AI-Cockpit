using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Cockpit.Infrastructure;
using Cockpit.Infrastructure.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Core.Tests.Updates;

/// <summary>
/// The two halves of AC-385: the hook that has to run before anything else, and the reading of whether this copy is
/// one the updater installed.
/// </summary>
public class VelopackBootstrapTests
{
    private const string TheHook = "VelopackApp.Build().Run();";

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
        var lines = File.ReadAllLines(_ProgramPath());

        var main = Array.FindIndex(lines, line => line.Contains("public static void Main(", StringComparison.Ordinal));
        Assert.True(main >= 0, "Program.cs no longer declares a 'public static void Main(' — this test reads that method's body");

        var opening = Array.FindIndex(lines, main, line => line.Trim() == "{");
        Assert.True(opening > main, "no opening brace found after Main's signature");

        var firstStatement = lines.Skip(opening + 1)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal));

        Assert.Equal(TheHook, firstStatement);
    }

    /// <summary>
    /// The test host is not a Velopack installation, so this is the real reading of the real environment — the same
    /// answer a developer gets from <c>dotnet run</c> and a packager gets from the tarball. It must be an answer and
    /// not an exception: the ticket's premise was that constructing the probe throws off a Velopack install, and it
    /// does not. This is where that is measured rather than assumed.
    /// </summary>
    [Fact]
    public void TheProbe_UnderATestHost_ReportsNotPackagedWithoutThrowing()
    {
        var support = new VelopackUpdateSupportProbe().Detect();

        Assert.Equal(UpdateSupport.NotPackaged, support);
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

namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1373: the backend suite must not reach Cockpit.App, nor Cockpit.TestSupport, which brings the Avalonia package.
/// A project reference copies its assembly into the output even before any code uses it, so the output folder tells.
/// </summary>
public class ArchitectureTests
{
    [Theory]
    [InlineData("Cockpit.App.dll")]
    [InlineData("Cockpit.TestSupport.dll")]
    public void TheBackendSuite_ShipsWithoutTheDesktopAssemblies(string assembly)
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "Cockpit.Infrastructure.dll")));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, assembly)));
    }

    // AC-1380 acceptance 4: the seven planners moved off Avalonia's DispatcherTimer onto TimeProvider/ITimer — a
    // stray `using Avalonia.Threading;` left on one of them would put an Avalonia type back into Infrastructure's
    // own IL, which this catches even though Infrastructure ships no Avalonia package reference of its own.
    [Fact]
    public void Infrastructure_ReferencesNoAvaloniaAssembly()
    {
        var referenced = typeof(Cockpit.Infrastructure.Sessions.SessionWatcher).Assembly.GetReferencedAssemblies();

        Assert.DoesNotContain(referenced, assembly => assembly.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true);
    }
}

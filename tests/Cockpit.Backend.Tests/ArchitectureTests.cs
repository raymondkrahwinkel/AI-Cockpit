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
}

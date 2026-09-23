namespace Cockpit.Backend.Tests;

/// <summary>
/// AC-1373: this suite is the backend without the desktop, so it must not be able to reach Cockpit.App, nor
/// Cockpit.TestSupport, which brings the Avalonia package with it. A project reference copies the assembly into
/// the output even when no code uses it yet, so the output folder is what tells.
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

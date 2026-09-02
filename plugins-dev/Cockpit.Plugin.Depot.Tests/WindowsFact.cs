namespace Cockpit.Plugin.Depot.Tests;

// Windows-only behaviour: xUnit reports a skip elsewhere, where a body returning early reports an unearned pass.
// A copy per test assembly, like PosixFactAttribute — neither sees the other's internals, and four lines are not
// worth a shared package reference.
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string windowsOnly) => Skip = OperatingSystem.IsWindows() ? null : windowsOnly;
}

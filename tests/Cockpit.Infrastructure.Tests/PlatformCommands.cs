namespace Cockpit.Infrastructure.Tests;

// A test whose subject is POSIX-only behaviour. The reason must say where the other platform is covered instead.
internal sealed class PosixFactAttribute : FactAttribute
{
    public PosixFactAttribute(string windowsCoverage) => Skip = OperatingSystem.IsWindows() ? windowsCoverage : null;
}

// A test whose subject is Windows-only behaviour. The reason says why the other platforms have nothing to prove,
// and xUnit reports it as a skip there — a body that returns early instead reports a pass it never earned.
internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string windowsOnly) => Skip = OperatingSystem.IsWindows() ? null : windowsOnly;
}

// A test whose subject is Linux-only behaviour (procfs, termios, coredumpctl). Separate from PosixFact because
// macOS is a POSIX host that has none of these, so it must skip there too.
internal sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute(string linuxOnly) => Skip = OperatingSystem.IsLinux() ? null : linuxOnly;
}

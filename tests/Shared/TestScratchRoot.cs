using System.Runtime.CompilerServices;

namespace Cockpit.Tests.Shared;

internal static class TestScratchRoot
{
    // AC-1999: a Release test process shares the operator's state-root fingerprint, so any orphan sweep it runs over
    // the default C:\ck removes every live session's scratch directory. Each test process gets its own root instead.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Isolate()
    {
        // A child of the default base, so scratch paths stay within the 20 characters SessionBaseInstructionTests
        // holds them to; no sweep reaches it, because sweeps only read markers in the base's direct children.
        var basis = OperatingSystem.IsWindows() ? @"C:\ck" : Path.Combine(Path.GetTempPath(), "ck");
        var root = Path.Combine(basis, _Base36(Environment.ProcessId));
        Environment.SetEnvironmentVariable("COCKPIT_SCRATCH_ROOT", root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover directory is harmless, a throwing exit handler is not.
            }
        };
    }

    private static string _Base36(int value)
    {
        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var text = string.Empty;
        do
        {
            text = digits[value % 36] + text;
            value /= 36;
        }
        while (value > 0);
        return text;
    }
}

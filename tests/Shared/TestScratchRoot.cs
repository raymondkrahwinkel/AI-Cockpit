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
        var root = Path.Combine(Path.GetTempPath(), "cockpit-test-scratch", Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("COCKPIT_SCRATCH_ROOT", root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover directory under TEMP is harmless, a throwing exit handler is not.
            }
        };
    }
}

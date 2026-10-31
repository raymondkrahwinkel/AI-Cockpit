using System.Runtime.CompilerServices;
using Microsoft.Diagnostics.Runtime;

namespace Cockpit.App.Diagnostics;

internal static class WindowsUiThreadStack
{
    // Keep ClrMD behind the caller's Windows check, including JIT-time assembly loading.
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Capture(int managedThreadId)
    {
        using var target = DataTarget.CreateSnapshotAndAttach(Environment.ProcessId);
        using var runtime = target.ClrVersions.Single().CreateRuntime();
        var thread = runtime.Threads.Single(thread => thread.ManagedThreadId == managedThreadId);
        var frames = thread.EnumerateStackTrace().Take(40)
            .Select(frame => frame.Method)
            .OfType<ClrMethod>()
            .Select(method => $"{method.Type?.Name}.{method.Name}");
        return string.Join(" <- ", frames);
    }
}

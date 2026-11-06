using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cockpit.Plugin.Browser.UI.Engine;

// AC-2058: Chromium's shutdown handler replaces the host's even when DisableSignalHandlers is set.
internal sealed class PosixTerminationHandler : IDisposable
{
    private const int SigTerm = 15;

    private readonly SignalAction _original;

    public PosixTerminationHandler()
    {
        if (_Read(SigTerm, nint.Zero, out _original) != 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not preserve the host SIGTERM handler.");
        }
    }

    public void Dispose()
    {
        if (_Restore(SigTerm, in _original, nint.Zero) != 0)
        {
            Trace.TraceWarning($"Browser: could not restore the host SIGTERM handler: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
    }

    // Opaque storage covers Linux (152 bytes) and macOS (16 bytes) without assuming their field layouts.
    [StructLayout(LayoutKind.Sequential, Size = 256)]
    private struct SignalAction { }

    [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
    private static extern int _Read(int signal, nint action, out SignalAction previous);

    [DllImport("libc", EntryPoint = "sigaction", SetLastError = true)]
    private static extern int _Restore(int signal, in SignalAction action, nint previous);
}

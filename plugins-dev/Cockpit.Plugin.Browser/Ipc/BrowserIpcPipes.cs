using System.IO.Pipes;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.Ipc;

// The launcher must restrict inheritance to these two client handles and close its client copies after launch.
internal sealed class BrowserIpcPipes : IDisposable
{
    internal AnonymousPipeServerStream Input { get; } = new(PipeDirection.In, HandleInheritability.Inheritable);
    internal AnonymousPipeServerStream Output { get; } = new(PipeDirection.Out, HandleInheritability.Inheritable);

    internal void ChildStarted()
    {
        Input.DisposeLocalCopyOfClientHandle();
        Output.DisposeLocalCopyOfClientHandle();
    }

    internal static (AnonymousPipeClientStream Input, AnonymousPipeClientStream Output) OpenChild(
        string inputHandle, string outputHandle)
    {
        var input = new AnonymousPipeClientStream(PipeDirection.In, inputHandle);
        try
        {
            var output = new AnonymousPipeClientStream(PipeDirection.Out, outputHandle);
            try
            {
                _StopInheritance(input.SafePipeHandle);
                _StopInheritance(output.SafePipeHandle);
                return (input, output);
            }
            catch
            {
                output.Dispose();
                throw;
            }
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private static void _StopInheritance(SafePipeHandle handle)
    {
        bool success;
        if (OperatingSystem.IsWindows())
        {
            success = _SetHandleInformation(handle, 1, 0);
        }
        else
        {
            success = _Fcntl(handle, 2, 1) == 0;
        }
        if (success == false)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    private static extern bool _SetHandleInformation(SafePipeHandle handle, uint mask, uint flags);

    [DllImport("libc", EntryPoint = "fcntl", SetLastError = true)]
    private static extern int _Fcntl(SafePipeHandle handle, int command, int value);

    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
    }
}

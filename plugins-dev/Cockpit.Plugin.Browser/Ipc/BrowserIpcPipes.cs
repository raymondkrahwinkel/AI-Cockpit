using System.IO.Pipes;

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
            return (input, new AnonymousPipeClientStream(PipeDirection.Out, outputHandle));
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using Cockpit.Plugin.Browser.Host;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed class LinuxBrowserHostProcess : IDisposable
{
    private readonly AnonymousPipeServerStream _lifetime = new(PipeDirection.Out, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream _errors = new(PipeDirection.In, HandleInheritability.Inheritable);

    internal int HostProcessId { get; private set; }

    [SupportedOSPlatform("linux")]
    internal async Task<Process> StartAsync(string executable, IReadOnlyList<string> arguments, BrowserIpcPipes pipes,
        CancellationToken cancellationToken)
    {
        using var ready = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        string[] childArguments = arguments.Select(argument =>
            argument.StartsWith("--cockpit-input=", StringComparison.Ordinal) ? "--cockpit-input=3" :
            argument.StartsWith("--cockpit-output=", StringComparison.Ordinal) ? "--cockpit-output=4" : argument).ToArray();
        int pid = LinuxBrowserProcess.Spawn(executable, ["--cockpit-supervisor", .. childArguments],
        [
            pipes.Output.ClientSafePipeHandle.DangerousGetHandle().ToInt32(),
            pipes.Input.ClientSafePipeHandle.DangerousGetHandle().ToInt32(),
            _lifetime.ClientSafePipeHandle.DangerousGetHandle().ToInt32(),
            ready.ClientSafePipeHandle.DangerousGetHandle().ToInt32(),
        ], standardError: _errors.ClientSafePipeHandle.DangerousGetHandle().ToInt32());
        try
        {
            _lifetime.DisposeLocalCopyOfClientHandle();
            ready.DisposeLocalCopyOfClientHandle();
            _errors.DisposeLocalCopyOfClientHandle();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            byte[] bytes = new byte[sizeof(int)];
            await ready.ReadExactlyAsync(bytes, timeout.Token).ConfigureAwait(false);
            HostProcessId = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (HostProcessId <= 0 || HostProcessId == pid)
            {
                throw new IOException("The browser supervisor did not start an isolated host.");
            }
            return Process.GetProcessById(pid);
        }
        catch
        {
            Stop();
            await Task.Run(() =>
            {
                if (OperatingSystem.IsLinux())
                {
                    Reap(pid);
                }
            }).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            ReportFailure();
            throw;
        }
        finally
        {
            GC.KeepAlive(pipes);
        }
    }

    [SupportedOSPlatform("linux")]
    internal static void Reap(int pid) => LinuxBrowserProcess.Wait(pid, out _);

    internal void Stop() => _lifetime.Dispose();

    internal void ReportFailure()
    {
        using var reader = new StreamReader(_errors, leaveOpen: true);
        string? reason = reader.ReadLine();
        if (string.IsNullOrEmpty(reason) == false)
        {
            Trace.TraceError(reason);
        }
    }

    public void Dispose()
    {
        _lifetime.Dispose();
        _errors.Dispose();
    }
}

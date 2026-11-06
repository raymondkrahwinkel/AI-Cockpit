using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using Cockpit.Plugin.Browser.Ipc;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed class BrowserHostClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BrowserIpcPipes _pipes;
    private readonly Task _exited;
    private readonly Task _closeOnExit;
    private int _disposed;

    private BrowserHostClient(Process process, BrowserIpcPipes pipes, Guid generation)
    {
        _process = process;
        _pipes = pipes;
        _exited = OperatingSystem.IsLinux() ? _ReapLinuxAsync(process.Id) : process.WaitForExitAsync();
        Generation = generation;
        Connection = new BrowserIpcConnection(pipes.Input, pipes.Output, generation,
            (_, _) => throw new InvalidDataException("Unknown engine client operation."), (generation, "enginehost"));
        _closeOnExit = _CloseOnExitAsync();
    }

    internal Guid Generation { get; }
    internal int ProcessId => _process.Id;
    internal BrowserIpcConnection Connection { get; }

    internal static async Task<BrowserHostClient> StartAsync(string executable, string runtimeDirectory, string cacheDirectory,
        string bufferDirectory, bool hardwareAcceleration, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows() == false && OperatingSystem.IsLinux() == false)
        {
            throw new PlatformNotSupportedException("The browser host is supported on Windows and Linux.");
        }
        executable = Path.GetFullPath(executable);
        runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        cacheDirectory = BrowserIpcStorage.PrepareCache(cacheDirectory);
        bufferDirectory = BrowserIpcStorage.Prepare(bufferDirectory);
        var generation = Guid.NewGuid();
        var pipes = new BrowserIpcPipes();
        BrowserHostClient? client = null;
        try
        {
            string[] arguments =
            [
                "--cockpit-enginehost",
                "--cockpit-cef-runtime-dir=" + runtimeDirectory,
                "--cockpit-cache-root=" + cacheDirectory,
                "--cockpit-buffer-root=" + bufferDirectory,
                "--cockpit-generation=" + generation.ToString("N"),
                "--cockpit-input=" + pipes.Output.GetClientHandleAsString(),
                "--cockpit-output=" + pipes.Input.GetClientHandleAsString(),
                .. hardwareAcceleration ? new[] { "--cockpit-hardware-acceleration" } : Array.Empty<string>(),
            ];
            cancellationToken.ThrowIfCancellationRequested();
            Process process;
            if (OperatingSystem.IsWindows())
            {
                process = WindowsBrowserHostProcess.Start(executable, arguments, pipes);
            }
            else if (OperatingSystem.IsLinux())
            {
                process = LinuxBrowserHostProcess.Start(executable, arguments, pipes);
            }
            else
            {
                throw new PlatformNotSupportedException("The browser host is supported on Windows and Linux.");
            }
            client = new BrowserHostClient(process, pipes, generation);
            pipes.ChildStarted();
            var ready = await client.Connection.CallAsync(generation, "enginehost", "engine.ready", JsonSerializer.SerializeToElement(true),
                BrowserIpcLimits.Deadline, cancellationToken).ConfigureAwait(false);
            if (ready.GetProperty("Initialized").GetBoolean() == false || ready.GetProperty("ProcessId").GetInt32() != process.Id)
            {
                throw new IOException("The browser host could not initialize CEF.");
            }
            return client;
        }
        catch
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                pipes.Dispose();
            }
            throw;
        }
    }

    [SupportedOSPlatform("linux")]
    private static Task _ReapLinuxAsync(int processId) => Task.Run(() => LinuxBrowserHostProcess.Reap(processId));

    private async Task _CloseOnExitAsync()
    {
        try
        {
            await _exited.ConfigureAwait(false);
        }
        finally
        {
            Connection.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        var errors = new List<Exception>();
        try
        {
            await Connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        BrowserIpcCleanup.Try(_pipes.Dispose, errors);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _exited.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Only the process started by this client is eligible for forced cleanup (tree ownership: AC-2145/2146).
            BrowserIpcCleanup.Try(() => _process.Kill(), errors);
            try
            {
                await _exited.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                errors.Add(exception);
            }
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        finally
        {
            _process.Dispose();
        }
        try
        {
            await _closeOnExit.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            errors.Add(exception);
        }
        if (errors.Count != 0)
        {
            throw new AggregateException("Browser host cleanup failed.", errors);
        }
    }
}

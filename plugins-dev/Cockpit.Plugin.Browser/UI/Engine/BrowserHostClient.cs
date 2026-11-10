using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Collections.Concurrent;
using Cockpit.Plugin.Browser.Ipc;
using Microsoft.Win32.SafeHandles;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal sealed class BrowserHostClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly SafeFileHandle? _job;
    private readonly LinuxBrowserHostProcess? _linux;
    private readonly BrowserIpcPipes _pipes;
    private readonly Task _exited;
    private readonly Task _closeOnExit;
    private int _disposed;
    private readonly ConcurrentDictionary<Guid, Func<BrowserIpcMessage, CancellationToken, Task<JsonElement>>> _tabs = new();

    private BrowserHostClient(Process process, BrowserIpcPipes pipes, Guid generation, SafeFileHandle? job, LinuxBrowserHostProcess? linux)
    {
        _process = process;
        _job = job;
        _linux = linux;
        _pipes = pipes;
        _exited = OperatingSystem.IsLinux() ? _ReapLinuxAsync(process.Id) : process.WaitForExitAsync();
        Generation = generation;
        Connection = new BrowserIpcConnection(pipes.Input, pipes.Output, generation,
            (message, token) => _tabs.TryGetValue(message.TabId, out var handle)
                ? handle(message, token) : throw new InvalidDataException("Unknown engine client operation."), (generation, "enginehost"),
            message => message.Operation == "tab.files");
        _closeOnExit = _CloseOnExitAsync();
    }

    internal Guid Generation { get; }
    internal int ProcessId => OperatingSystem.IsLinux() && _linux is not null ? _linux.HostProcessId : _process.Id;
    internal BrowserIpcConnection Connection { get; }
    internal string BufferDirectory { get; private set; } = string.Empty;

    internal void RegisterTab(Guid id, Func<BrowserIpcMessage, CancellationToken, Task<JsonElement>> handle)
    {
        Connection.RegisterTab(id, "operator");
        if (_tabs.TryAdd(id, handle) == false)
        {
            throw new InvalidOperationException("The browser tab is already registered.");
        }
    }

    internal async Task UnregisterTabAsync(Guid id)
    {
        await Connection.UnregisterTabAsync(id, "operator").ConfigureAwait(false);
        _tabs.TryRemove(id, out _);
    }

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
        SafeFileHandle? job = null;
        Process? process = null;
        LinuxBrowserHostProcess? linux = null;
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
            if (OperatingSystem.IsWindows())
            {
                (process, job) = WindowsBrowserHostProcess.Start(executable, arguments, pipes);
            }
            else if (OperatingSystem.IsLinux())
            {
                linux = new LinuxBrowserHostProcess();
                process = await linux.StartAsync(executable, arguments, pipes, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new PlatformNotSupportedException("The browser host is supported on Windows and Linux.");
            }
            client = new BrowserHostClient(process, pipes, generation, job, linux);
            client.BufferDirectory = bufferDirectory;
            pipes.ChildStarted();
            var ready = await client.Connection.CallAsync(generation, "enginehost", "engine.ready", JsonSerializer.SerializeToElement(true),
                BrowserIpcLimits.Deadline, cancellationToken).ConfigureAwait(false);
            if (ready.GetProperty("Initialized").GetBoolean() == false || ready.GetProperty("ProcessId").GetInt32() != client.ProcessId)
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
                job?.Dispose();
                linux?.Dispose();
                process?.Dispose();
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
            try
            {
                _job?.Dispose();
                _linux?.ReportFailure();
            }
            finally
            {
                _linux?.Dispose();
                Connection.Close();
            }
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
            if (_job is not null)
            {
                _job.Dispose();
            }
            else
            {
                _linux?.Stop();
            }
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
            _job?.Dispose();
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
        _tabs.Clear();
        if (errors.Count != 0)
        {
            throw new AggregateException("Browser host cleanup failed.", errors);
        }
    }
}

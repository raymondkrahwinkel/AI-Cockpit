using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Browser;

// AC-1712: One headless Chromium per session, with a throwaway profile and its own debug port on loopback. Playwright
// MCP's `--cdp-endpoint` takes the default context of the browser it connects to, so a process per session is what
// keeps two sessions' tabs apart. Owns the processes: they stop with the session and with the host, never later.
internal sealed class AgentBrowserHost(Func<string?> resolveExecutable, string profilesRoot, ILogger? logger = null) : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.Ordinal);
    private bool _disposed;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly Dictionary<string, long> _epochs = new(StringComparer.Ordinal);

    public async Task<string?> GetOrCreateAsync(string paneId, CancellationToken cancellationToken = default)
    {
        long epoch;
        lock (_gate) { epoch = _epochs.GetValueOrDefault(paneId); }
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed || _epochs.GetValueOrDefault(paneId) != epoch) return null;
            }
            if (SessionFor(paneId) is { } current)
            {
                return current.Endpoint;
            }

            lock (_gate)
            {
                if (_disposed || _running.ContainsKey(paneId)) return null;
            }
            if (resolveExecutable() is not { } executable) return null;
            return await StartAsync(paneId, executable, Environment.IsPrivilegedProcess, cancellationToken, epoch).ConfigureAwait(false);
        }
        finally { _startGate.Release(); }
    }

    public sealed record Session(Guid Generation, string Endpoint, string Upstream, bool OperatorDrives);

    public Session? SessionFor(string paneId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(paneId, out var running) && running is { Stopped: false, Proxy: not null, Upstream: not null }
                && !running.Exited
                ? new Session(running.Generation, running.Proxy.Endpoint, running.Upstream, running.Proxy.OperatorDrives) : null;
        }
    }

    // The set of live agent browsers or who drives one changed (AC-1713).
    public event Action? Changed;

    // Starts (or restarts, with a fresh empty profile) the browser of one session and returns its CDP endpoint,
    // or null when there is no browser to start or it did not come up.
    public async Task<string?> StartAsync(string paneId, CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await StartCoreAsync(paneId, cancellationToken).ConfigureAwait(false); }
        finally { _startGate.Release(); }
    }

    private async Task<string?> StartCoreAsync(string paneId, CancellationToken cancellationToken)
    {
        Stop(paneId);
        if (resolveExecutable() is not { } executable)
        {
            return null;
        }

        // A container runs as root, where Chromium refuses to start its sandbox.
        return await StartAsync(paneId, executable, Environment.IsPrivilegedProcess, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> StartAsync(string paneId, string executable, bool noSandbox, CancellationToken cancellationToken, long? expectedEpoch = null)
    {
        long epoch;
        lock (_gate)
        {
            if (_disposed) return null;
            epoch = _epochs.GetValueOrDefault(paneId);
            if (expectedEpoch is { } expected && epoch != expected) return null;
        }
        var profile = Path.Combine(profilesRoot, Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(profile);
        }
        else
        {
            Directory.CreateDirectory(profile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var errors = new ConcurrentQueue<string>();
        var process = Launch(executable, profile, errors, noSandbox);
        if (process is null)
        {
            DeleteProfile(profile);
            return null;
        }

        WriteOwner(process, profile);

        var running = new Running(process, profile);
        lock (_gate)
        {
            if (_disposed || _epochs.GetValueOrDefault(paneId) != epoch)
            {
                running.Stop();
                return null;
            }

            _running[paneId] = running;
        }

        int? port;
        try { port = await ReadPortAsync(running, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { Stop(paneId); throw; }
        if (port is null)
        {
            // Said out loud: a browser that dies before it listens ("No usable sandbox!" on Ubuntu 23.10+) is otherwise
            // only a session without an endpoint.
            lock (_gate)
            {
                if (!_running.TryGetValue(paneId, out var current) || current != running) return null;
            }
            var exited = running.Exited;
            if (exited)
            {
                // The async overload also waits for the redirected stderr to drain, so the reason is in `errors`.
                using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await running.Process.WaitForExitAsync(drain.Token).ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false); }
                catch (InvalidOperationException) { }
            }

            Stop(paneId);
            var reason = string.Join(" | ", errors);

            // A kernel or seccomp profile that refuses the sandbox its namespaces kills Chromium at start; Playwright's own Linux default is no sandbox.
            if (exited && !noSandbox && OperatingSystem.IsLinux() && IsSandboxFailure(reason))
            {
                Report(LogLevel.Warning, $"the agent browser '{executable}' has no usable sandbox here, so it runs without one: {reason}");
                return await StartAsync(paneId, executable, noSandbox: true, cancellationToken, epoch + 1).ConfigureAwait(false);
            }

            Report(LogLevel.Error, $"the agent browser '{executable}' did not come up ({(exited ? "process exited" : $"no port within {StartTimeout.TotalSeconds:0}s")}): {reason}");
            return null;
        }

        // Playwright is given the proxy's endpoint (AC-1713); the browser's own stays with the host.
        lock (_gate)
        {
            if (_disposed || !_running.TryGetValue(paneId, out var current) || current != running) return null;
            running.Upstream = $"http://127.0.0.1:{port}";
            running.Proxy = AgentCdpProxy.Start(port.Value);
        }
        Changed?.Invoke();
        return running.Proxy.Endpoint;
    }

    public string? EndpointFor(string paneId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(paneId, out var running) ? running.Proxy?.Endpoint : null;
        }
    }

    // The sessions with an agent browser that is up, and the browser's own endpoint, for the tabs view (AC-1713).
    public IReadOnlyList<(string PaneId, string Upstream, bool OperatorDrives)> Live()
    {
        lock (_gate)
        {
            return [.. _running.Where(pair => pair.Value is { Stopped: false, Upstream: not null, Proxy: not null }).Select(pair => (pair.Key, pair.Value.Upstream!, pair.Value.Proxy!.OperatorDrives))];
        }
    }

    // Raymond takes the surface of one session, or hands it back. False when the session has no live browser.
    public bool SetOperatorDrives(string paneId, bool drives)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(paneId, out var running) is false || running is not { Stopped: false, Proxy: { } proxy })
            {
                return false;
            }

            if (drives)
            {
                proxy.TakeOver();
            }
            else
            {
                proxy.Release();
            }
        }

        Changed?.Invoke();
        return true;
    }

    // Stop agent: the browser goes, the proxy stays so the session's next call reads why (AC-1713).
    public bool StopBrowser(string paneId)
    {
        Running? running;
        lock (_gate)
        {
            if (_running.TryGetValue(paneId, out running) is false || running.Stopped)
            {
                return false;
            }

            running.Stopped = true;
        }

        running.Proxy?.MarkStopped();
        running.KillBrowser();
        Changed?.Invoke();
        return true;
    }

    internal int? ProcessIdFor(string paneId)
    {
        lock (_gate)
        {
            return _running.TryGetValue(paneId, out var running) ? running.Process.Id : null;
        }
    }

    public void Stop(string paneId)
    {
        Running? running;
        lock (_gate)
        {
            _running.Remove(paneId, out running);
            _epochs[paneId] = _epochs.GetValueOrDefault(paneId) + 1;
        }

        if (running is not null)
        {
            running.Stop();
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        List<Running> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _running.Values];
            _running.Clear();
        }

        foreach (var running in all)
        {
            running.Stop();
        }
    }

    // A hard kill of the host leaves its browsers running (nothing ties them to it), so each profile names the process
    // that owns it: process id and start time, the pair that cannot be a recycled id.
    private const string OwnerFile = "cockpit-owner.txt";

    // Ends the browsers a previous run left behind and clears their profiles, as StaleSessionProcessSweep does for
    // sessions on Linux. Only what this host's own profile root names; run once at start, before any session.
    public void SweepStale()
    {
        if (!Directory.Exists(profilesRoot))
        {
            return;
        }

        foreach (var profile in Directory.EnumerateDirectories(profilesRoot))
        {
            if (StopOwner(Path.Combine(profile, OwnerFile))) DeleteProfile(profile);
        }
    }

    private static void WriteOwner(Process process, string profile)
    {
        try
        {
            File.WriteAllLines(
                Path.Combine(profile, OwnerFile),
                [process.Id.ToString(CultureInfo.InvariantCulture), process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)]);
        }
        catch (InvalidOperationException)
        {
            // Exited already: nothing to name, and nothing to leave behind.
        }
    }

    private static bool StopOwner(string ownerFile)
    {
        try
        {
            var lines = File.Exists(ownerFile) ? File.ReadAllLines(ownerFile) : [];
            if (lines.Length < 2 || !int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            {
                return false;
            }

            using var process = Process.GetProcessById(id);
            if (Math.Abs(process.StartTime.ToUniversalTime().Ticks - ticks) < TimeSpan.TicksPerSecond)
            {
                return Shutdown(process, Path.GetDirectoryName(ownerFile)!);
            }
            return false;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or Win32Exception)
        {
            // Without a verified root we cannot prove that its descendants have released the profile.
            return false;
        }
    }

    private static bool Shutdown(Process process, string profile)
    {
        using var tree = new BrowserProcessTree(process);
        tree.Capture();
        try { CloseAsync(profile).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException or System.Net.WebSockets.WebSocketException or InvalidOperationException) { }
        if (tree.Wait(TimeSpan.FromSeconds(5))) return true;
        tree.Kill();
        return tree.Wait(TimeSpan.FromSeconds(5));
    }

    private static async Task CloseAsync(string profile)
    {
        if (!TryParsePort(Path.Combine(profile, "DevToolsActivePort"), out var port)) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        using var version = JsonDocument.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", timeout.Token).ConfigureAwait(false));
        using var browser = await CdpPage.ConnectAsync(version.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!, (_, _) => { }, timeout.Token).ConfigureAwait(false);
        await browser.SendAsync("Browser.close").WaitAsync(timeout.Token).ConfigureAwait(false);
    }

    // Chromium's own words when it cannot set up its sandbox: user namespaces refused, or no setuid helper to fall back on.
    internal static bool IsSandboxFailure(string stderr) =>
        stderr.Contains("No usable sandbox", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Failed to move to new namespace", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("setuid sandbox", StringComparison.OrdinalIgnoreCase);

    private void Report(LogLevel level, string message)
    {
        if (logger is null)
        {
            Console.Error.WriteLine($"browser: {message}");
            return;
        }

        logger.Log(level, "{Message}", message);
    }

    private static Process? Launch(string executable, string profile, ConcurrentQueue<string> errors, bool noSandbox)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in new[]
        {
            "--headless=new",
            "--remote-debugging-port=0",
            "--remote-debugging-address=127.0.0.1",
            $"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "about:blank",
        })
        {
            start.ArgumentList.Add(argument);
        }

        if (noSandbox)
        {
            start.ArgumentList.Add("--no-sandbox");
        }

        try
        {
            var process = Process.Start(start);

            // The first lines only: they say why a start failed, and a long-lived browser keeps writing.
            if (process is not null)
            {
                process.ErrorDataReceived += (_, line) =>
                {
                    if (line.Data is { Length: > 0 } text && errors.Count < 20)
                    {
                        errors.Enqueue(text);
                    }
                };
            }

            // Unread, the redirected pipes would fill and stall the browser.
            process?.BeginOutputReadLine();
            process?.BeginErrorReadLine();
            return process;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // Chromium writes `DevToolsActivePort` (port, then the browser's path) into the profile once it listens.
    private static async Task<int?> ReadPortAsync(Running running, CancellationToken cancellationToken)
    {
        var file = Path.Combine(running.Profile, "DevToolsActivePort");
        using var timeout = new CancellationTokenSource(StartTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        try
        {
            while (!running.Exited)
            {
                if (TryParsePort(file, out var port))
                {
                    return port;
                }

                await Task.Delay(50, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
        }

        return null;
    }

    private static bool TryParsePort(string file, out int port)
    {
        port = 0;
        try
        {
            var lines = File.Exists(file) ? File.ReadAllLines(file) : [];
            return lines.Length >= 2 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out port) && port > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static void DeleteProfile(string path)
    {
        // Chromium lets go of its profile a moment after it exits.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }

    private sealed class Running(Process process, string profile)
    {
        public Guid Generation { get; } = Guid.NewGuid();
        public bool Exited
        {
            get
            {
                try { return Process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }
        public Process Process { get; } = process;

        public string Profile { get; } = profile;

        public string? Upstream { get; set; }

        public AgentCdpProxy? Proxy { get; set; }

        public bool Stopped { get; set; }

        private bool _ended;

        public void Stop()
        {
            Proxy?.Dispose();
            KillBrowser();
        }

        public void KillBrowser()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            try
            {
                if (!Shutdown(Process, Profile)) return;
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            Process.Dispose();
            DeleteProfile(Profile);
        }
    }
}

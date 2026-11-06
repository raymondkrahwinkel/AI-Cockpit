using System.Diagnostics;
using System.Threading.Channels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using ModelContextProtocol.Client;
using Cockpit.Infrastructure.Sessions;
using ModelContextProtocol.Protocol;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class SessionStdioClientTransport(
    McpServerConfig server, SessionMcpContext? context, ISessionProcessAnchor? anchor, string? paneId, SessionStdioProcesses? processes = null) : IClientTransport
{
    public string Name => server.Name;

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo
        {
            FileName = server.Command ?? string.Empty,
            WorkingDirectory = context?.WorkingDirectory ?? Directory.GetCurrentDirectory(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in server.Args) { start.ArgumentList.Add(argument); }
        if (OperatingSystem.IsLinux())
        {
            // AC-2130: the parent-death signal kills the isolated process group, including servers that ignore stdin EOF.
            start.ArgumentList.Clear();
            foreach (var argument in new[] { "--pdeathsig", "TERM", "/usr/bin/setsid", "/bin/sh", "-c", "trap '/bin/kill -KILL -- -$$' TERM; [ \"$PPID\" -eq \"$1\" ] || /bin/kill -KILL -- -$$; shift; \"$@\" <&0 & wait \"$!\"", "cockpit-stdio", Environment.ProcessId.ToString(), start.FileName })
            {
                start.ArgumentList.Add(argument);
            }
            foreach (var argument in server.Args) { start.ArgumentList.Add(argument); }
            start.FileName = "/usr/bin/setpriv";
        }
        var environment = StdioServerEnvironment.Build();
        foreach (var (key, value) in context?.Environment ?? new Dictionary<string, string?>())
        {
            environment[key] = value;
        }
        start.Environment.Clear();
        foreach (var (key, value) in StdioServerEnvironment.Build(environment))
        {
            if (value is not null) { start.Environment[key] = value; }
        }
        var process = new Process { StartInfo = start };
        IDisposable? lifetime = null;
        var processId = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            processId = process.Id;
            if (OperatingSystem.IsWindows())
            {
                lifetime = anchor?.Anchor(process.Id, paneId)
                    ?? throw new InvalidOperationException("The stdio server could not be anchored to this session.");
            }
            if (paneId is not null) { processes?.Add(paneId, process.Id); }
            // Drain stderr without logging it: third-party diagnostics may contain credentials.
            var stderr = _DrainAsync(process.StandardError);
            var transport = await new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream).ConnectAsync(cancellationToken);
            return new SessionTransport(transport, process, lifetime, stderr, processes);
        }
        catch
        {
            if (processId != 0) { processes?.Remove(processId); }
            lifetime?.Dispose();
            _Kill(process);
            process.Dispose();
            throw;
        }
    }

    private static async Task _DrainAsync(StreamReader reader)
    {
        var buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer) > 0) { }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
        }
    }

    private static void _Kill(Process process)
    {
        try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        catch (InvalidOperationException) { }
    }

    private sealed class SessionTransport(ITransport inner, Process process, IDisposable? anchor, Task stderr, SessionStdioProcesses? processes) : ITransport
    {
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync();
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) { _Kill(process); }
            }
            finally
            {
                processes?.Remove(process.Id);
                anchor?.Dispose();
                _Kill(process);
                process.StandardError.Close();
                await stderr;
                process.Dispose();
            }
        }
    }
}

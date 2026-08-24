using Porta.Pty;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

// Hosts a child process inside a Unix pty via Porta.Pty (`forkpty()`/`execvp()` run
// entirely in its native shim, sidestepping the .NET 7+ W^X/fork pitfall), so the child sees a
// real interactive terminal — the Linux/macOS counterpart to `ConPtyProcess`.
internal sealed class PortaPtyProcess : IConPtyProcess
{
    private readonly IPtyConnection _connection;

    public Stream InputStream => _connection.WriterStream;

    public Stream OutputStream => _connection.ReaderStream;

    public int ProcessId => _connection.Pid;

    private PortaPtyProcess(IPtyConnection connection)
    {
        _connection = connection;
    }

    // AC-1013: Spawns `executablePath` with `arguments` as argv and `environment` as its exact environment. On
    // Linux, `PtyTerminalModes.WrapForSaneModes` first execs a shell that fixes the pty's line disciplines (handed
    // to us all cleared — AC-129); `SpawnAsync`'s Linux `forkpty()` is synchronous, matching `ITtyLauncher.Launch`'s own synchronous contract, so we block here rather than threading `Task` through the call chain.
    public static PortaPtyProcess Start(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        short columns,
        short rows)
    {
        var (app, commandLine) = PtyTerminalModes.WrapForSaneModes(executablePath, arguments);

        var options = new PtyOptions
        {
            Name = "xterm-256color",
            App = app,
            CommandLine = commandLine,
            Cwd = workingDirectory,
            Cols = columns,
            Rows = rows,
            Environment = new Dictionary<string, string>(environment, StringComparer.Ordinal),
        };

        var connection = PtyProvider.SpawnAsync(options, CancellationToken.None).GetAwaiter().GetResult();
        return new PortaPtyProcess(connection);
    }

    public void Resize(short columns, short rows) => _connection.Resize(columns, rows);

    // IPtyConnection.Dispose() already disposes the reader/writer streams and sends the child a
    // SIGHUP (swallowing ESRCH if it already exited) — the same "signal closure, let it exit"
    // teardown ConPtyProcess uses on Windows (there via ClosePseudoConsole's implicit EOF).
    public void Dispose() => _connection.Dispose();
}

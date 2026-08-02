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

    // Spawns `executablePath` inside a fresh pty of the given size, in
    // `workingDirectory`, with `arguments` as its argv (Porta.Pty's
    // Unix provider builds `execvp`'s argv as `[executablePath, ...arguments, null]` — see
    // `Porta.Pty.Unix.PtyProvider.GetExecvpArgs`) and exactly `environment` as
    // its environment (Porta.Pty overlays this onto the inherited process environment; since
    // `environment` already carries that base plus our overrides, the result is
    // the same dictionary reaching the child).
    //
    // On Linux the launch goes through `PtyTerminalModes.WrapForSaneModes` first: a shell that fixes
    // the pty's line disciplines and then `exec`s this executable, because the pty is handed to us with them
    // all cleared (AC-129). The `exec` keeps the pid, the signals and the process tree as they were.
    // `PtyProvider.SpawnAsync` is only genuinely asynchronous on its Windows path; the
    // Linux/macOS `forkpty()` syscall it wraps is synchronous. `ITtyLauncher.Launch`
    // is itself a synchronous contract (mirrors `ConPtyProcess.Start`, called from a UI
    // event handler before the terminal control exists to await anything), so we block on the
    // already-fast spawn here rather than threading `Task` through the whole call chain.
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

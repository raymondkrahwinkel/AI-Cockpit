using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// A TTY session that owns the files launched with it — the provider's session-scoped files (an MCP config
/// handed to the CLI) and its status snapshot. All are deleted when the session is disposed.
/// <para>
/// An MCP config carries the registry's bearer headers and the CLI only reads it while starting up. Tying a
/// file's lifetime to the session's is what keeps a credential from outliving the thing that needed it — the
/// version before this wrote one per session and deleted none.
/// </para>
/// </summary>
internal sealed class TtyProcessOwningSessionFiles(
    IConPtyProcess inner,
    IReadOnlyList<string> sessionScopedFiles,
    string? statusFile = null)
    : IConPtyProcess, ITtyStatusFile
{
    public Stream InputStream => inner.InputStream;

    public Stream OutputStream => inner.OutputStream;

    public int ProcessId => inner.ProcessId;

    public string? StatusFile => statusFile;

    public void Resize(short columns, short rows) => inner.Resize(columns, rows);

    public void Dispose()
    {
        inner.Dispose();

        foreach (var path in sessionScopedFiles)
        {
            _Delete(path);
        }

        if (statusFile is not null)
        {
            _Delete(statusFile);
        }
    }

    /// <summary>Best-effort: a session that has already exited must not fail on its own cleanup. Whatever
    /// survives is swept on the next start (<c>TtyMcpConfigFile.SweepStale</c>, <c>StatusLineRelay.SweepStale</c>).</summary>
    private static void _Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Swept on the next start — a locked or already-removed file is not worth an error.
        }
    }
}

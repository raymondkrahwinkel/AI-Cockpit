using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions.Tty;

// AC-1013: A TTY session that owns what was minted or written for it — the provider's session-scoped files, its
// status snapshot, and (when it had a pane id) its per-session MCP keyring token (AC-89, AC-143) — dropping all
// of it on dispose, so a credential never outlives the session, and revoking the token here since the pty's end is otherwise unreachable from `SessionMcpKeyring`.
internal sealed class TtyProcessOwningSessionFiles(
    IConPtyProcess inner,
    IReadOnlyList<string> sessionScopedFiles,
    string? statusFile = null,
    SessionMcpKeyring? keyring = null,
    string? paneId = null,
    string? token = null,
    IDisposable? memoryCap = null)
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

        // AC-143: this pane's bearer must not survive the session that owned it — dropped by the minter (this
        // wrapper wraps the process TtyLauncher minted the token for), never logged (the value is the secret).
        if (keyring is not null && paneId is not null && token is not null)
        {
            keyring.Revoke(paneId, token);
        }

        // AC-661: the job object/cgroup this session ran in. Released after the process, so the group is empty by
        // the time it is torn down.
        memoryCap?.Dispose();
    }

    // Best-effort: a session that has already exited must not fail on its own cleanup. Whatever
    // survives is swept on the next start (`TtyMcpConfigFile.SweepStale`, and the provider plugin sweeps its own statusline files).
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

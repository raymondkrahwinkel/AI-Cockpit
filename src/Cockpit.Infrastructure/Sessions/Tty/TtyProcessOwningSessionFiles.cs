using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Sessions.Tty;

// A TTY session that owns what was minted or written for it: the provider's session-scoped files (an MCP config
// handed to the CLI), its status snapshot, and — when this session had a pane id — its per-session MCP keyring
// token (AC-89, AC-143). All are dropped when the session is disposed.
//
// An MCP config carries the registry's bearer headers and the CLI only reads it while starting up. Tying a
// file's lifetime to the session's is what keeps a credential from outliving the thing that needed it — the
// version before this wrote one per session and deleted none.
//
// The keyring token is revoked here — `TtyLauncher`'s own mint site — rather than through any shared
// cross-component teardown path: the pty's end is otherwise only visible to the app layer, which cannot reach
// `SessionMcpKeyring`, so this wrapper is where the TTY route's own teardown lives.
internal sealed class TtyProcessOwningSessionFiles(
    IConPtyProcess inner,
    IReadOnlyList<string> sessionScopedFiles,
    string? statusFile = null,
    SessionMcpKeyring? keyring = null,
    string? paneId = null,
    string? token = null)
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

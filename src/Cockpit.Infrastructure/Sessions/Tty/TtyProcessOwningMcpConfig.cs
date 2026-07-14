using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// A TTY session that owns the files launched with it: the <c>--mcp-config</c> handed to the CLI, and the
/// statusline snapshot its header reads. Both are deleted when the session is disposed.
/// <para>
/// The MCP config carries the registry's bearer headers, and the CLI only reads it while starting up. Tying a
/// file's lifetime to the session's is what keeps a credential from outliving the thing that needed it — the
/// version before this wrote one per session and deleted none.
/// </para>
/// </summary>
internal sealed class TtyProcessOwningMcpConfig(IConPtyProcess inner, string? mcpConfigPath, string? statusFile = null)
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

        if (mcpConfigPath is not null)
        {
            TtyMcpConfigFile.Delete(mcpConfigPath);
        }

        if (statusFile is not null)
        {
            StatusLineRelay.Delete(statusFile);
        }
    }
}

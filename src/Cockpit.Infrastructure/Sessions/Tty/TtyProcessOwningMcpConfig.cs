using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions.Tty;

/// <summary>
/// A TTY session that owns its <c>--mcp-config</c> file: the file is deleted when the session is disposed.
/// <para>
/// The file carries the registry's bearer headers, and the CLI only reads it while starting up. Tying its
/// lifetime to the session's is what keeps a credential from outliving the thing that needed it — the previous
/// version wrote one per session and deleted none.
/// </para>
/// </summary>
internal sealed class TtyProcessOwningMcpConfig(IConPtyProcess inner, string mcpConfigPath) : IConPtyProcess
{
    public Stream InputStream => inner.InputStream;

    public Stream OutputStream => inner.OutputStream;

    public int ProcessId => inner.ProcessId;

    public void Resize(short columns, short rows) => inner.Resize(columns, rows);

    public void Dispose()
    {
        inner.Dispose();
        TtyMcpConfigFile.Delete(mcpConfigPath);
    }
}

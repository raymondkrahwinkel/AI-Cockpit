using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.BackendApi;

// AC-2288: the terminal launcher of a pane over a server. Nothing starts on this machine: the server chose the shell,
// folder, profile and MCP, so the provider and options the pane hands in are not used. `sessionPaneId` names a TTY
// session already running there; without it a new shell opens there.
internal sealed class RemoteTtyLauncher(BackendApiClient client, string? sessionPaneId) : ITtyLauncher
{
    // Called off the UI thread by the view, as a local launch is, so the open may block.
    public IConPtyProcess Launch(
        ITtySessionProvider provider,
        SessionProfile? profile,
        IReadOnlyDictionary<string, string> options,
        short columns,
        short rows,
        string? workingDirectory = null,
        SessionResume? resume = null,
        string? paneId = null,
        IReadOnlySet<string>? enabledMcpServerNames = null,
        SessionResources? contributed = null,
        string? projectId = null,
        Action<string>? notice = null,
        Action<string?>? reportModel = null)
    {
        if (sessionPaneId is not null)
        {
            var pty = new RemotePty(client, sessionPaneId, closeOnDispose: false);
            pty.Resize(columns, rows);
            return pty;
        }

        var opened = client.SendAsync<JsonElement>(HttpMethod.Post, "api/v1/terminals", new { columns = (int)columns, rows = (int)rows })
            .GetAwaiter().GetResult();
        notice?.Invoke($"Terminal on {opened.GetProperty("node").GetString()}, in {opened.GetProperty("workingDirectory").GetString()}.");
        return new RemotePty(client, opened.GetProperty("id").GetString() ?? throw new JsonException("The server opened a terminal without an id."), closeOnDispose: true);
    }
}

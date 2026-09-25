using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

// AC-1398: a backend part embeds a session in a workspace by that workspace's id, down the same route the workspace's
// own EmbedSession takes, so the host still ties the session to that workspace and ends it when the workspace closes.
public class DesktopPluginHostEmbedSessionTests
{
    [Fact]
    public void EmbedSession_EmbedsInTheNamedWorkspaceThroughTheShell()
    {
        var request = new EmbeddedSessionRequest { ProfileId = "ceo" };
        var session = Substitute.For<IEmbeddedSession>();
        var shell = new Shell(session);
        var services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IEmbeddedSessionHost)).Returns(shell);
        ICockpitHost host = new DesktopPluginHost(
            "autopilot",
            "Autopilot",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());

        var embedded = host.EmbedSession("workspace-2", request);

        Assert.Equal((session, "workspace-2", request), (embedded, shell.WorkspaceId, shell.Request));
    }

    // Internal to the app, so a hand fake rather than a substitute; it records where it was asked to embed.
    private sealed class Shell(IEmbeddedSession session) : IEmbeddedSessionHost
    {
        public string? WorkspaceId { get; private set; }

        public EmbeddedSessionRequest? Request { get; private set; }

        public IEmbeddedSession Embed(string workspaceId, EmbeddedSessionRequest request)
        {
            WorkspaceId = workspaceId;
            Request = request;
            return session;
        }

        public void CloseForWorkspace(string workspaceId)
        {
        }

        public Control? EmbeddedSessionView(string paneId) => null;
    }
}

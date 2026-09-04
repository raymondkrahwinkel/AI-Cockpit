using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Mcp;
using Cockpit.Core.Toasts;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Whether the startup notice speaks. A restored cockpit keeps its Depot connection and loses the credential, so
/// the one thing that must happen is being told without going looking for it — and the one thing that must not is
/// a warning on every start of a cockpit where nothing is wrong.
/// </summary>
public class McpSignInNoticeTests
{
    /// <summary>
    /// The state a restore leaves behind: the server is configured, its stored token is empty, so
    /// <see cref="McpAuthState.AuthorizationRequired"/> — and the operator hears about it at startup.
    /// </summary>
    [Fact]
    public async Task AServerWaitingToBeSignedInTo_IsAnnouncedOnceWithAWayToDoIt()
    {
        var toasts = _NewNotice(out var notice, ("Depot: depot.test", McpAuthState.AuthorizationRequired));

        await notice.CheckAsync();

        toasts.Received(1).Show(
            Arg.Is<string>(message => message.Contains("Depot: depot.test")),
            ToastSeverity.Warning,
            "Sign in",
            Arg.Any<Action>());
    }

    /// <summary>
    /// The real risk of this change: it runs on every start, so a cockpit whose sign-ins are all good must stay
    /// silent. A notice shown when nothing is wrong is one the operator learns to click away unread, which costs
    /// the times it matters.
    /// </summary>
    [Fact]
    public async Task WhenEverySignInIsGood_NothingIsSaid()
    {
        var toasts = _NewNotice(
            out var notice,
            ("Depot: depot.test", McpAuthState.Authorized),
            ("a-plain-server", McpAuthState.NotRequired));

        await notice.CheckAsync();

        toasts.DidNotReceiveWithAnyArgs().Show(default!, default);
    }

    private static IToastService _NewNotice(out McpSignInNotice notice, params (string Name, McpAuthState State)[] servers)
    {
        var configs = servers
            .Select(server => new McpServerConfig { Name = server.Name, Auth = McpServerAuth.OAuth })
            .ToList();

        var catalog = Substitute.For<IMcpServerCatalog>();
        catalog.GetServersAsync(Arg.Any<CancellationToken>()).Returns<IReadOnlyList<McpServerConfig>>(configs);

        var oauth = Substitute.For<IMcpOAuthCoordinator>();
        oauth.GetStateAsync(Arg.Any<McpServerConfig>(), Arg.Any<CancellationToken>()).Returns(
            call => servers.First(server => server.Name == call.ArgAt<McpServerConfig>(0).Name).State);

        var toasts = Substitute.For<IToastService>();
        notice = new McpSignInNotice(catalog, oauth, toasts, NullLogger<McpSignInNotice>.Instance);

        return toasts;
    }
}

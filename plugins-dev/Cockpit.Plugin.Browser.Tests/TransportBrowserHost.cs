using System.Reflection;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Browser.Tests;

public class TransportBrowserHost : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_CurrentMcpCaller" => McpRequestContext.CurrentCaller,
        "get_Channel" => DispatchProxy.Create<ICockpitHost, AppKeyHost>().Channel,
        "get_Sessions" => NullCockpitSessionObserver.Instance,
        "RequestConsentAsync" => Task.FromResult(ConsentDecision.Denied),
        _ => throw new InvalidOperationException($"Unexpected browser host call: {targetMethod?.Name}"),
    };
}

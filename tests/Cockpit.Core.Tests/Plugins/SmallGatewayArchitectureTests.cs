using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Verify;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// AC-1374 acceptance 2 and AC-1375 acceptance 3: <c>Cockpit.App</c> carries no implementation of the gateway seams
/// those tickets moved to Infrastructure — a regression here means one came back into App rather than staying moved.
/// </summary>
public class SmallGatewayArchitectureTests
{
    [Theory]
    [InlineData(typeof(IAssistantReadGateway))]
    [InlineData(typeof(IWorkspaceAgentGateway))]
    [InlineData(typeof(IVerifySessionGateway))]
    [InlineData(typeof(ISessionLabelSink))]
    [InlineData(typeof(IAssistantAgentGateway))]
    public void CockpitApp_ImplementsNoneOfTheMovedGatewaySeams(Type seam)
    {
        var offenders = typeof(CockpitViewModel).Assembly.GetTypes()
            .Where(candidate => candidate is { IsClass: true, IsAbstract: false } && seam.IsAssignableFrom(candidate))
            .ToList();

        Assert.Empty(offenders);
    }
}

using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workflows;

namespace Cockpit.Plugin.Workflows.Engine;

/// <summary>
/// The one place a <see cref="WorkflowEngine"/> is built (#69). The editor runs a flow because you pressed Execute;
/// the watcher runs one because a trigger fired — and if those two built their own engines, a flow would be able to
/// do different things depending on who started it, which is the sort of difference nobody would think to test for.
/// </summary>
internal static class EngineFactory
{
    public static WorkflowEngine Create(ICockpitHost host, IReadOnlyList<IWorkflowStep> contributed) =>
        new([
            new ManualTriggerRunner(),
            new TriggerRunner("cockpit.text-match"),
            new TriggerRunner("cockpit.schedule"),
            new NotifyRunner(host),
            new InjectRunner(host),
            new StartSessionRunner(host),
            new CommandRunner(),
            new HttpRunner(),
            new IfRunner(),
            new ApproveRunner(host),
            .. contributed.Select(step => new ContributedStep(step)),
        ]);
}

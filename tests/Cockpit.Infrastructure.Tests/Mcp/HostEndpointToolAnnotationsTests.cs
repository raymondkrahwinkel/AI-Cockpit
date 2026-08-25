using System.Reflection;
using ModelContextProtocol.Server;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Shell;
using Cockpit.Infrastructure.Terminal;
using Cockpit.Infrastructure.Verify;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// AC-1066: every <c>[McpServerTool]</c> on the six host-owned endpoints (<c>cockpit-session</c>,
/// <c>cockpit-agents</c>, <c>cockpit-worktrees</c>, <c>cockpit-verify</c>, <c>cockpit-terminal</c>,
/// <c>cockpit-shell</c>) must carry an explicit read-only or destructive hint. Without one, <see
/// cref="DelegatedToolPermissionPolicy.Classify"/> reports <see cref="ToolPermissionClass.Unknown"/>, which is
/// denied at every ceiling — including <c>bypassPermissions</c> — for a delegated session (AC-79). This test failed
/// on all six before this ticket, since none of them carried an annotation at all.
/// </summary>
public class HostEndpointToolAnnotationsTests
{
    private static readonly Type[] HostEndpointToolTypes =
    [
        typeof(SessionStatusTools),
        typeof(AgentsMcpTools),
        typeof(WorktreeTools),
        typeof(VerifyMcpTools),
        typeof(TerminalMcpTools),
        typeof(ShellMcpTools),
    ];

    public static IEnumerable<object[]> HostEndpointTools() =>
        HostEndpointToolTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(method => new object[] { type, method }));

    [Theory]
    [MemberData(nameof(HostEndpointTools))]
    public void EveryTool_CarriesAnExplicitReadOnlyOrDestructiveHint(Type toolsType, MethodInfo method)
    {
        var (readOnly, destructive) = _HintsOf(method);

        Assert.True(
            readOnly.HasValue || destructive.HasValue,
            $"{toolsType.Name}.{method.Name} has no explicit ReadOnly/Destructive on its [McpServerTool] — it classifies as Unknown and is denied at every delegated ceiling, including bypassPermissions.");
    }

    [Fact]
    public void RunCommand_ClassifiesAsDestructive_SoOnlyBypassPermissionsRunsItUnattended()
    {
        var method = typeof(ShellMcpTools).GetMethod(nameof(ShellMcpTools.RunCommand))!;
        var (readOnly, destructive) = _HintsOf(method);

        var toolClass = DelegatedToolPermissionPolicy.Classify(readOnly, destructive);
        Assert.Equal(ToolPermissionClass.Destructive, toolClass);

        // The pitfall this ticket names by name: Classify(false, false) would give Write, which would let the
        // shell already run at acceptEdits and erase the whole ceiling distinction. Only Destructive is correct.
        foreach (var ceiling in new[] { "default", "plan", "acceptEdits" })
        {
            Assert.False(DelegatedToolPermissionPolicy.Decide(ceiling, toolClass, "run_command", onAllowList: false).IsAllowed);
        }

        Assert.True(DelegatedToolPermissionPolicy.Decide("bypassPermissions", toolClass, "run_command", onAllowList: false).IsAllowed);
    }

    // Reads the attribute's own nullable backing fields (ModelContextProtocol.Core internals) rather than its
    // public ReadOnly/Destructive properties, which always report a bool (false/true) — the properties cannot
    // tell "explicitly set" apart from "never touched", which is exactly the distinction this test exists to make.
    private static (bool? ReadOnly, bool? Destructive) _HintsOf(MethodInfo method)
    {
        var attribute = method.GetCustomAttribute<McpServerToolAttribute>()!;
        var readOnlyField = typeof(McpServerToolAttribute).GetField("_readOnly", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var destructiveField = typeof(McpServerToolAttribute).GetField("_destructive", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return ((bool?)readOnlyField.GetValue(attribute), (bool?)destructiveField.GetValue(attribute));
    }
}

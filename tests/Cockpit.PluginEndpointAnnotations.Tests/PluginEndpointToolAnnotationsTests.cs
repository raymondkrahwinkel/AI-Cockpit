using System.Reflection;
using ModelContextProtocol.Server;
using Cockpit.Core.Sessions.Permissions;

namespace Cockpit.PluginEndpointAnnotations.Tests;

/// <summary>
/// AC-1070: every plugin <c>[McpServerTool]</c> needs an explicit read-only/destructive hint (AC-1066's rule,
/// extended to plugins-dev/) or <see cref="DelegatedToolPermissionPolicy.Classify"/> reports <see
/// cref="ToolPermissionClass.Unknown"/>, denied at every delegated ceiling including bypassPermissions. Tool
/// classes are <c>internal</c> per plugin, so this loads each assembly by name and reflects over its types —
/// why it lives in its own project rather than in one plugin's own .Tests.
/// </summary>
public class PluginEndpointToolAnnotationsTests
{
    private static readonly string[] PluginAssemblyNames =
    [
        "Cockpit.Plugin.Autopilot",
        "Cockpit.Plugin.Diagram",
        "Cockpit.Plugin.Docker",
        "Cockpit.Plugin.GitHubPullRequests",
        "Cockpit.Plugin.Kubernetes",
        "Cockpit.Plugin.LocalCi",
        "Cockpit.Plugin.Proxmox",
        "Cockpit.Plugin.Workflows",
        "Cockpit.Plugin.YouTrack",
    ];

    public static IEnumerable<object[]> PluginEndpointTools() =>
        PluginAssemblyNames
            .Select(Assembly.Load)
            .SelectMany(_LoadableTypes)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                .Select(method => new object[] { type, method }));

    // A plugin assembly also carries Avalonia view types this project never references — GetTypes() throws the
    // moment one needs an Avalonia assembly this project never pulled in. The resolved types ride the exception.
    private static IEnumerable<Type> _LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null)!;
        }
    }

    // Lives in its own project (rather than inside one plugin's .Tests) because it is the one guard that must see
    // every plugin at once — no single plugin's own test project can reference the other eight.
    [Theory]
    [MemberData(nameof(PluginEndpointTools))]
    public void EveryTool_CarriesAnExplicitReadOnlyOrDestructiveHint(Type toolsType, MethodInfo method)
    {
        var (readOnly, destructive) = _HintsOf(method);
        var toolName = method.GetCustomAttribute<McpServerToolAttribute>()?.Name ?? method.Name;

        Assert.True(
            readOnly.HasValue || destructive.HasValue,
            $"{toolsType.FullName}.{method.Name} (tool \"{toolName}\") has no explicit ReadOnly/Destructive on its [McpServerTool] — it classifies as Unknown and is denied at every delegated ceiling, including bypassPermissions.");
    }

    [Fact]
    public void RunLocalChecks_ClassifiesAsDestructive_SoOnlyBypassPermissionsRunsItUnattended()
    {
        var assembly = Assembly.Load("Cockpit.Plugin.LocalCi");
        var type = assembly.GetType("Cockpit.Plugin.LocalCi.Mcp.LocalCiMcpTools")
            ?? throw new InvalidOperationException("Cockpit.Plugin.LocalCi.Mcp.LocalCiMcpTools not found — did the plugin move or rename?");
        var method = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>()?.Name == "run_local_checks");
        var (readOnly, destructive) = _HintsOf(method);

        var toolClass = DelegatedToolPermissionPolicy.Classify(readOnly, destructive);
        Assert.Equal(ToolPermissionClass.Destructive, toolClass);

        // The pitfall AC-1070 names by name: Classify(false, false) would give Write, which would let a tool
        // that runs arbitrary project-defined CI commands already run at acceptEdits. Only Destructive is correct.
        foreach (var ceiling in new[] { "default", "plan", "acceptEdits" })
        {
            Assert.False(DelegatedToolPermissionPolicy.Decide(ceiling, toolClass, "run_local_checks", onAllowList: false).IsAllowed);
        }

        Assert.True(DelegatedToolPermissionPolicy.Decide("bypassPermissions", toolClass, "run_local_checks", onAllowList: false).IsAllowed);
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

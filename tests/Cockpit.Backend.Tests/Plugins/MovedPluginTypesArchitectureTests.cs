using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Cockpit.Core;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1391: the pluginloader, storage and UI-free registries moved from Cockpit.App to this assembly without a
// behaviour change. These are the two acceptance tegenproefs the ticket asks for, plus a regression guard on the
// mechanism (the Infrastructure service scan) that wires the moved registries up without any App-side help.
public class MovedPluginTypesArchitectureTests
{
    public static IEnumerable<object[]> MovedTypes { get; } =
    [
        [typeof(PluginManager)], [typeof(PluginActivator)], [typeof(PluginLoadContext)], [typeof(PluginStorage)],
        [typeof(PluginCacheStore)], [typeof(PluginDiagnostics)], [typeof(PluginFailure)], [typeof(PluginIssueSeverity)],
        [typeof(PluginPendingApproval)], [typeof(PluginIntentRegistry)], [typeof(IPluginIntentRegistry)],
        [typeof(AutopilotTemplateRegistry)], [typeof(IAutopilotTemplateRegistry)],
        [typeof(ConversationPickerRegistry)], [typeof(IConversationPickerRegistry)],
        [typeof(WorkflowStepRegistry)], [typeof(IWorkflowStepRegistry)],
        [typeof(WorkflowTemplateRegistry)], [typeof(IWorkflowTemplateRegistry)],
        [typeof(TrackerProviderRegistry)], [typeof(ITrackerProviderRegistry)],
        [typeof(SessionResourceProviderRegistry)], [typeof(ISessionResourceProviderRegistry)],
        [typeof(SessionResourceResolver)],
        [typeof(ProjectMemorySourceRegistry)], [typeof(IProjectMemorySourceRegistry)], [typeof(ProjectMemorySourceMapping)],
        [typeof(ProjectOwnershipRegistry)], [typeof(IProjectOwnershipRegistry)],
        [typeof(ProjectMemoryNoteWriter)], [typeof(IProjectMemoryNoteWriter)],
        [typeof(McpOAuthTokenAdoption)],
        [typeof(GitDirectoryStatusResolver)],
    ];

    // Acceptance 1: neither the type's own assembly nor any of its constructor parameters reach back into
    // Cockpit.App or Avalonia*. Per type, not just per assembly, so a violation names the offending type.
    [Theory]
    [MemberData(nameof(MovedTypes))]
    public void MovedType_ReferencesNeitherAppNorAvalonia(Type type)
    {
        var referenced = type.Assembly.GetReferencedAssemblies().Select(name => name.Name);
        Assert.DoesNotContain(referenced, name => name == "Cockpit.App" || (name?.StartsWith("Avalonia", StringComparison.Ordinal) ?? false));

        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            foreach (var parameter in constructor.GetParameters())
            {
                Assert.True(
                    parameter.ParameterType.Assembly.GetName().Name != "Cockpit.App",
                    $"{type.Name}'s constructor takes {parameter.ParameterType.Name}, declared in Cockpit.App.");
            }
        }
    }

    // Acceptance 2. This does not need to compile-check the other two — it would simply not compile at all if
    // PluginManager, PluginActivator or PluginStorage were still in Cockpit.App, which Backend.Tests never references.
    [Fact]
    public void PluginManagerActivatorAndStorage_BuildAndRun_WithoutApp()
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics());
        manager.LoadAndConfigure([], new ServiceCollection(), _ => null);
        Assert.Empty(manager.Loaded);

        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);
        var discovered = new DiscoveredPlugin(
            "/plugins/missing", "missing",
            new PluginManifest("missing", "missing", "1.0", EntryAssembly: null, AbstractionsVersion: 1, EntryType: null, MinHostVersion: null, Description: null, Author: null),
            Sha256: "hash", PluginLoadDecision.Load);
        Assert.Null(activator.Activate(discovered));

        var storage = new PluginStorage(new Dictionary<string, string>(), _ => { });
        storage.Set("key", 42);
        Assert.Equal(42, storage.Get<int>("key"));
    }

    // Regression guard for the move itself: the ten moved registries carry ISingletonService and used to be
    // discovered by App's own assembly scan. Infrastructure's scan (already wired for its own ISingletonService
    // types) must pick them up the same way, with no registration left behind in Cockpit.App.
    [Fact]
    public void MovedRegistries_AreDiscoveredByTheInfrastructureServiceScan()
    {
        var provider = new ServiceCollection().AddLogging()
            .AddServices(typeof(Cockpit.Core.DependencyInjection).Assembly, typeof(Cockpit.Infrastructure.DependencyInjection).Assembly)
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IAutopilotTemplateRegistry>());
        Assert.NotNull(provider.GetRequiredService<IConversationPickerRegistry>());
        Assert.NotNull(provider.GetRequiredService<IPluginIntentRegistry>());
        Assert.NotNull(provider.GetRequiredService<ISessionResourceProviderRegistry>());
        Assert.NotNull(provider.GetRequiredService<IProjectMemorySourceRegistry>());
        Assert.NotNull(provider.GetRequiredService<IProjectOwnershipRegistry>());
        Assert.NotNull(provider.GetRequiredService<IProjectMemoryNoteWriter>());
        Assert.NotNull(provider.GetRequiredService<ITrackerProviderRegistry>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowStepRegistry>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowTemplateRegistry>());
    }
}

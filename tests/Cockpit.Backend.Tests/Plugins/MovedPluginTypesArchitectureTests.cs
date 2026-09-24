using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core;
using Cockpit.Infrastructure.Plugins;

namespace Cockpit.Backend.Tests.Plugins;

// AC-1391: the pluginloader, storage and UI-free registries moved from Cockpit.App without a behaviour change.
// AC-1392: PluginManager/PluginActivator followed once their UI phase moved to the desktop's PluginUiManager.
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

    // Acceptance 2, narrowed to PluginStorage — the one of the ticket's three that has no Avalonia-tainted surface.
    // This does not need to assert much: it would simply not compile if PluginStorage were still in Cockpit.App,
    // which Backend.Tests never references.
    [Fact]
    public void PluginStorage_BuildsAndRuns_WithoutApp()
    {
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

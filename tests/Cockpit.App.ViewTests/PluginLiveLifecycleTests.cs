using System.Collections;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Startup;
using Cockpit.Core.Workspaces;
using Cockpit.Infrastructure.Backup;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Backup;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.Health;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Settings;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Tracking;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Onboarding;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workflows;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

// AC-2022's generic test (layer 2, isolation): activate, deactivate, activate again, for a plugin that makes every
// kind of contribution. The registries are walked rather than listed, so a forgotten undo fails here.
[Collection("avalonia")]
public sealed class PluginLiveLifecycleTests
{
    private const string PluginId = "everything";
    private const string WidgetId = "everything.widget";
    private const string WorkspaceTypeId = "everything.workspace";

    [Fact]
    public Task APluginThatMakesEveryContribution_LeavesNothingBehind_AndComesBack() => HeadlessAvalonia.RunAsync(async () =>
    {
        var folder = Directory.CreateTempSubdirectory("cockpit-live-plugin").FullName;
        File.WriteAllText(
            Path.Combine(folder, "plugin.json"),
            $$"""{"id":"{{PluginId}}","name":"Everything","version":"1.0.0","entryAssembly":"everything.dll","abstractionsVersion":3}""");
        var sha256 = await PluginClosureHash.OfInstalledFolderAsync(folder);
        var discovered = new DiscoveredPlugin(
            folder, PluginId, new PluginManifest(PluginId, "Everything", "1.0.0", "everything.dll", 3, null, null, null, null), sha256, PluginLoadDecision.Load);

        var cockpit = new CockpitViewModel();
        var mcpEndpoints = Substitute.For<ICockpitMcpEndpointHost>();
        mcpEndpoints.MountAsync("", new object(), null, false).ReturnsForAnyArgs(true);
        var provider = _Registries(cockpit, mcpEndpoints);
        var workspaces = new WorkspacesViewModel(
            null, provider.GetRequiredService<IWidgetRegistry>(), null, provider.GetRequiredService<IWorkspaceTypeRegistry>());

        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics());
        manager.RunOnUiThread = work => UiThreadCall.Uncapped(() =>
        {
            work();
            return Task.CompletedTask;
        });
        var uiParts = new PluginUiManager(NullLogger<PluginUiManager>.Instance, new PluginDiagnostics(), manager);
        var parts = new List<EveryContribution>();
        var disposed = new List<string>();
        var bodiesClosed = 0;

        ICockpitPlugin Activate(DiscoveredPlugin _)
        {
            parts.Add(new EveryContribution(disposed));
            return parts[^1];
        }

        ICockpitHost HostFor(DiscoveredPlugin plugin, ICockpitPlugin _) => _RecordingHost(new DesktopBackendHost(
            plugin.FolderId, plugin.Manifest.Name, provider, Substitute.For<ICockpitActions>(), Substitute.For<IPluginStorage>(),
            Substitute.For<ICockpitSessionObserver>(), new PluginDiagnostics(), typeof(EveryContribution), null, [], manager.RuntimeFor(plugin.FolderId)));

        ICockpitUiHost UiHostFor(DiscoveredPlugin plugin, ICockpitPluginUi _) => _RecordingUiHost(new CockpitUiHost(
            plugin.FolderId, plugin.Manifest.Name, HostFor(plugin, parts[^1]), provider, cockpit, Substitute.For<IPluginDialogHost>(),
            Substitute.For<IPluginWindowActions>(), Substitute.For<IPluginActiveSession>(), [], manager.RuntimeFor(plugin.FolderId)));

        ICockpitPluginUi ActivateUi(DiscoveredPlugin _, ICockpitPlugin? __) => new EveryUiContribution(disposed, () => bodiesClosed++);

        manager.Activated += (plugin, backend) => Dispatcher.UIThread.Post(() => uiParts.InitializeUiOne(plugin, backend, ActivateUi, UiHostFor));
        manager.Deactivated += uiParts.DeactivateUi;

        var nothing = _Held(provider, cockpit);
        manager.LoadAndConfigure([discovered], new ServiceCollection(), Activate);
        manager.Initialize(HostFor);
        uiParts.InitializeUi(ActivateUi, UiHostFor);
        await workspaces.OpenWorkspaceAsync(WorkspaceTypeId);
        var pluginDesk = workspaces.Settings.ActiveWorkspaceId;
        Assert.NotNull(workspaces.ActivePluginBody);
        await workspaces.AddWorkspaceOptionCommand.ExecuteAsync(new WorkspaceMenuOption("Board", MaterialIconKind.Abacus, "", WorkspaceType.Dashboard));
        var board = workspaces.Settings.ActiveWorkspaceId;
        await workspaces.AddWidgetAsync(WidgetId);
        Assert.Single(workspaces.WidgetPanes);

        var active = _Held(provider, cockpit);
        Assert.Empty(_ContributionKindsNotMade());
        Assert.All(nothing.Keys.Select(_Owner).Distinct(), owner =>
            Assert.Contains(active, held => _Owner(held.Key) == owner && held.Value > nothing.GetValueOrDefault(held.Key)));

        // An undo that throws must not keep the ones recorded before it from running.
        manager.RuntimeFor(PluginId).Add(() => throw new InvalidOperationException("undo failed"));
        var deactivated = await Task.Run(() => manager.DeactivateAsync(PluginId));

        Assert.Equal(PluginActivationStatus.Deactivated, deactivated.Status);
        Assert.Equal(nothing, _Held(provider, cockpit));
        Assert.Equal(["ui", "backend"], disposed);
        Assert.Equal(1, bodiesClosed);
        mcpEndpoints.ReceivedWithAnyArgs(1).Retire("", "");
        Assert.Empty(workspaces.WidgetPanes);

        await workspaces.SelectWorkspaceCommand.ExecuteAsync(pluginDesk);
        Assert.True(workspaces.ShowUnknownPluginWorkspace);
        Assert.Equal("This workspace's plugin is disabled.", workspaces.UnknownPluginWorkspaceTitle);
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(board);

        // Twice is a no-op, not a second Dispose.
        Assert.Equal(PluginActivationStatus.NotActive, (await manager.DeactivateAsync(PluginId)).Status);
        Assert.Equal(2, disposed.Count);

        Assert.Equal(PluginActivationStatus.Activated, (await Task.Run(() => manager.ActivateOne(discovered))).Status);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(active, _Held(provider, cockpit));
        Assert.Single(workspaces.WidgetPanes);
        await workspaces.SelectWorkspaceCommand.ExecuteAsync(pluginDesk);
        Assert.NotNull(workspaces.ActivePluginBody);
    });

    // The owner of a counted collection: "WidgetRegistry._widgets" belongs to "WidgetRegistry".
    private static string _Owner(string held) => held[..held.IndexOf('.')];

    // Every collection a holder keeps, by count; what a plugin leaves behind shows as a count that did not go back.
    private static Dictionary<string, int> _Held(IServiceProvider provider, CockpitViewModel cockpit)
    {
        var held = new Dictionary<string, int>();
        foreach (var registry in _RegistryTypes.Select(provider.GetRequiredService))
        {
            _Count(held, registry.GetType(), registry, field => field.Name != "_withdrawn");
        }

        _Count(held, typeof(CockpitViewModel), cockpit, field => field.Name.Contains("lugin", StringComparison.Ordinal) || field.Name == "_settingsSavedHandlers");
        _Count(held, typeof(ToastHostViewModel), cockpit.ToastHost, field => field.Name == "_sources");
        _Count(held, typeof(MarkdownView), null, field => field.Name == "RegisteredLinkHandlers");
        return held;
    }

    private static void _Count(Dictionary<string, int> held, Type type, object? instance, Func<FieldInfo, bool> matches)
    {
        const BindingFlags Fields = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(Fields | (instance is null ? BindingFlags.Static : BindingFlags.Instance)).Where(matches))
        {
            if (field.GetValue(instance) is ICollection collection)
            {
                held[$"{type.Name}.{field.Name}"] = collection.Count;
            }
        }
    }

    private static readonly Type[] _RegistryTypes =
    [
        typeof(PluginChannelHub),
        typeof(IWidgetRegistry),
        typeof(IWorkspaceTypeRegistry),
        typeof(ICompanionToolRegistry),
        typeof(IFirstStartRouteRegistry),
        typeof(IDockPanelRegistry),
        typeof(IConversationPickerRegistry),
        typeof(IPluginProviderConfigViews),
        typeof(IPluginProviderRegistry),
        typeof(IPluginTtyProviderRegistry),
        typeof(IPluginIntentRegistry),
        typeof(IAutopilotTemplateRegistry),
        typeof(IWorkflowStepRegistry),
        typeof(IWorkflowTemplateRegistry),
        typeof(ITrackerProviderRegistry),
        typeof(ISessionResourceProviderRegistry),
        typeof(IProjectFieldRegistry),
        typeof(IProjectOwnershipRegistry),
        typeof(IProjectMemorySourceRegistry),
        typeof(ISharedProjectSourceRegistry),
        typeof(PluginHealthSections),
        typeof(SettingsCarriers),
        typeof(BackupTargets),
        typeof(StartupSequence),
    ];

    private static IServiceProvider _Registries(CockpitViewModel cockpit, ICockpitMcpEndpointHost mcpEndpoints)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(cockpit);
        services.AddSingleton(mcpEndpoints);
        services.AddSingleton(Substitute.For<IManagedCliService>());
        services.AddSingleton(new PluginChannelHub(NullLogger<PluginChannelHub>.Instance));
        services.AddSingleton<IWidgetRegistry, WidgetRegistry>();
        services.AddSingleton<IWorkspaceTypeRegistry, WorkspaceTypeRegistry>();
        services.AddSingleton<ICompanionToolRegistry, CompanionToolRegistry>();
        services.AddSingleton<IFirstStartRouteRegistry, FirstStartRouteRegistry>();
        services.AddSingleton<IDockPanelRegistry, DockPanelRegistry>();
        services.AddSingleton<IConversationPickerRegistry, ConversationPickerRegistry>();
        services.AddSingleton<IPluginProviderConfigViews, PluginProviderConfigViews>();
        services.AddSingleton<IPluginProviderRegistry, PluginProviderRegistry>();
        services.AddSingleton<IPluginTtyProviderRegistry, PluginTtyProviderRegistry>();
        services.AddSingleton<IPluginIntentRegistry, PluginIntentRegistry>();
        services.AddSingleton<IAutopilotTemplateRegistry, AutopilotTemplateRegistry>();
        services.AddSingleton<IWorkflowStepRegistry, WorkflowStepRegistry>();
        services.AddSingleton<IWorkflowTemplateRegistry, WorkflowTemplateRegistry>();
        services.AddSingleton<ITrackerProviderRegistry, TrackerProviderRegistry>();
        services.AddSingleton<ISessionResourceProviderRegistry, SessionResourceProviderRegistry>();
        services.AddSingleton<IProjectFieldRegistry, ProjectFieldRegistry>();
        services.AddSingleton<IProjectOwnershipRegistry, ProjectOwnershipRegistry>();
        services.AddSingleton<IProjectMemorySourceRegistry, ProjectMemorySourceRegistry>();
        services.AddSingleton<ISharedProjectSourceRegistry, SharedProjectSourceRegistry>();
        services.AddSingleton<PluginHealthSections>();
        services.AddSingleton<SettingsCarriers>();
        services.AddSingleton<BackupTargets>();
        services.AddSingleton(new StartupSequence());
        return services.BuildServiceProvider();
    }

    private static readonly HashSet<string> _kindsMade = new(StringComparer.Ordinal);

    private static ICockpitHost _RecordingHost(ICockpitHost host) => _Recording(host);

    private static ICockpitUiHost _RecordingUiHost(ICockpitUiHost host) => _Recording(host);

    private static T _Recording<T>(T target) where T : class
    {
        var proxy = DispatchProxy.Create<T, RecordingProxy>();
        ((RecordingProxy)(object)proxy).Target = target;
        return proxy;
    }

    // A new Add* on either host fails here until the test plugin makes that contribution too.
    private static IEnumerable<string> _ContributionKindsNotMade() =>
        typeof(ICockpitHost).GetMethods().Concat(typeof(ICockpitUiHost).GetMethods())
            .Select(method => method.Name)
            .Where(name => name.StartsWith("Add", StringComparison.Ordinal) || name.StartsWith("Register", StringComparison.Ordinal) || name == "ClaimProjectOwnership")
            .Distinct()
            .Except(_kindsMade);

    // Public, as DispatchProxy requires of the type it derives from.
    public class RecordingProxy : DispatchProxy
    {
        public object? Target { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            lock (_kindsMade)
            {
                _kindsMade.Add(targetMethod.Name);
            }

            return targetMethod.Invoke(Target, args);
        }
    }

    private sealed class EveryContribution(List<string> disposed) : ICockpitPlugin
    {
        public PluginMetadata Metadata { get; } = new(PluginId, "Everything", "1.0.0");

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Initialize(ICockpitHost host)
        {
            host.AddSupervisedActivityProvider(Substitute.For<ISupervisedActivitySource>());
            host.AddWorkflowStep(Substitute.For<IWorkflowStep>());
            host.AddWorkflowTemplate(new WorkflowTemplate("flow", "Flow", "", "{}"));
            host.RegisterIntentHandler("act", _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()));
            host.RegisterAutopilotTemplate(new PluginAutopilotTemplate("template", "Template", "body"));
            host.AddSessionProvider(new SessionProviderRegistration("everything-provider", "Everything", _ => Substitute.For<IPluginSessionDriverFactory>(), new PluginSessionCapabilities(false, false)));
            host.AddTtyProvider(new TtyProviderRegistration("everything-provider", "Everything", _ => Substitute.For<IPluginTtyProvider>(), []));
            _ = host.AddMcpServer(new McpServerContribution("everything-server", "http://localhost"));
            _ = host.AddMcpEndpoint("everything-endpoint", new object());
            host.AddNotificationSource(_ => Task.FromResult<PluginNotification?>(null));
            host.AddProjectField(new ProjectFieldRegistration("field", "Field", _ => Task.FromResult<IReadOnlyList<ProjectFieldOption>>([])));
            host.ClaimProjectOwnership(new ProjectOwnershipRegistration("project"));
            host.AddProjectMemorySource(new ProjectMemorySourceRegistration("memory", "Memory", ""));
            host.AddProjectMemorySourceFamily(new ProjectMemorySourceFamily("family", "Family"));
            host.AddTrackerProvider(Substitute.For<ITrackerProvider>());
            host.AddManagedCli(new ManagedCliDescriptor
            {
                CliName = "everything",
                ResolveLatestVersionAsync = (_, _) => Task.FromResult("1.0.0"),
                BuildDownloadPlanAsync = (_, _, _, _) => Task.FromException<ManagedCliDownloadPlan>(new NotSupportedException()),
            });
            host.AddSessionResourceProvider(Substitute.For<ISessionResourceProvider>());
            var source = Substitute.For<ISharedProjectSource>();
            source.Key.Returns("shared");
            host.AddSharedProjectSource(source);
            var section = Substitute.For<IPluginHealthSection>();
            section.Name.Returns("health");
            host.AddHealthSection(section);
            host.AddSettingsCarrier(new SettingsCarrierRegistration("carrier", "Carrier", "Address", _ => Task.FromResult(false), _ => Task.FromResult<byte[]?>(null), (_, _) => Task.CompletedTask));
            var target = Substitute.For<IBackupTarget>();
            target.Id.Returns("backup");
            host.AddBackupTarget(target);
            host.AddStartupStep("step", _ => Task.CompletedTask);
            host.AddFileSource(Substitute.For<IFileSourceProvider>());
        }

        public void Dispose() => disposed.Add("backend");
    }

    private sealed class EveryUiContribution(List<string> disposed, Action bodyClosed) : ICockpitPluginUi, IDisposable
    {
        public void InitializeUi(ICockpitUiHost host)
        {
            host.AddSettings(() => new TextBlock());
            host.AddServerSettings(_ => new TextBlock());
            host.AddFirstStartRoute(new FirstStartRoute("route", "Route", "", () => new TextBlock()));
            host.AddSideMenuButton("Button", () => { });
            host.AddSideMenuButtonWithBadge("Badge", () => { });
            host.AddSideMenuSection("Section", () => new TextBlock());
            host.AddSessionHeaderItem(_ => new TextBlock());
            host.AddSessionBanner(_ => new TextBlock());
            host.AddSessionHeaderAction(new PluginSessionAction("Action", "", _ => { }));
            host.AddToolbarAction(new ToolbarAction("Tool", null, () => Task.CompletedTask));
            host.AddShortcut(new PluginShortcut("shortcut", "Shortcut", "Ctrl+Alt+E", () => { }));
            host.AddConversationPicker(new ConversationPickerRegistration("Picker", () => Task.FromResult<string?>(null)));
            host.AddProviderConfigView("everything-provider", _ => Substitute.For<IPluginProviderConfigView>());
            host.AddWidget(new WidgetRegistration(WidgetId, "Widget", _ => new TextBlock()));
            host.AddDockPanel(new DockPanelRegistration("everything.panel", "Panel", MaterialIconKind.Abacus, () => new TextBlock()));
            host.AddCompanionTool(new CompanionToolRegistration("everything.tool", "Tool", _ => new TextBlock()));
            host.AddWorkspaceType(new WorkspaceTypeRegistration(WorkspaceTypeId, "Everything", context =>
            {
                context.Closed += (_, _) => bodyClosed();
                return new TextBlock();
            }));
            host.AddFileWorkspaceType("everything.file", "File");
            host.AddLinkHandler(new LinkHandlerRegistration(_NeverMatches, (_, _) => false));
        }

        private static bool _NeverMatches(string text, out int start, out int length)
        {
            start = length = 0;
            return false;
        }

        public void Dispose() => disposed.Add("ui");
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Plugins;
using Cockpit.Core.Tests.Voice;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

public sealed class PluginStoreSourcesTests : IDisposable
{
    private readonly string _directory = Path.Combine(AppContext.BaseDirectory, "plugin-source-tests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("organization:tools")]
    public async Task LiveSources_ReachTheManager_AndOnlyTheirHiddenKeysPersist(string sourceKey)
    {
        Directory.CreateDirectory(_directory);
        var configPath = Path.Combine(_directory, "cockpit.json");
        var configured = PluginStoreConfig.Remote("https://public.example/index.json");
        var configStore = new PluginStoreConfigStore(configPath);
        await configStore.AddAsync(configured);
        var sources = new PluginStoreSources(configStore, configPath);
        var notifications = 0;
        sources.SourcesChanged += (_, _) => notifications++;
        Assert.Equal(new PluginStoreSourceInfo(configured), Assert.Single(await sources.LoadAsync()));

        using var failedProvider = sources.Add(new FailingProvider());
        var provider = new MutableProvider();
        using var registration = sources.Add(provider);
        var tokenCalls = 0;
        provider.Change([
            new("organization:slow", "Slow", "https://slow.example/index.json", "tools", _ => ValueTask.FromResult<string?>(null)),
            new(sourceKey, "Private tools", "https://private.example/index.json", "tools", _ =>
            {
                tokenCalls++;
                return ValueTask.FromResult<string?>("request-only-credential");
            }),
            new("organization:failed", "Failed", "https://failed.example/index.json", "tools", _ => ValueTask.FromResult<string?>(null)),
        ]);
        Assert.Equal(3, notifications);
        Assert.Equal(4, (await sources.LoadAsync()).Count);

        await sources.SetHiddenAsync(sourceKey, true);
        var reloaded = new PluginStoreSources(configStore, configPath);
        using var reloadedRegistration = reloaded.Add(provider);
        var hidden = Assert.Single(await reloaded.LoadAsync(), source => source.Store.Signed?.SourceKey == sourceKey);
        Assert.True(hidden.IsHidden);
        Assert.Equal("Private tools", hidden.Name);
        Assert.Equal(0, tokenCalls);
        var json = await File.ReadAllTextAsync(configPath);
        Assert.Contains(sourceKey, json);
        Assert.DoesNotContain("private.example", json);
        Assert.DoesNotContain("request-only-credential", json);
        Assert.DoesNotContain("Signed", json);
        var backup = await File.ReadAllTextAsync(Path.ChangeExtension(configPath, "json.bak"));
        Assert.DoesNotContain("private.example", backup);
        Assert.DoesNotContain("request-only-credential", backup);
        await sources.SetHiddenAsync(sourceKey, false);

        var administration = Substitute.For<IPluginAdministration>();
        administration.GetInstalledAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<InstalledPlugin>>([]));
        var slow = new TaskCompletionSource<PluginStoreFetchResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var empty = new PluginStoreFetchResult(true, null, new PluginStoreIndex(null, []), configured.Location);
        administration.FetchStoreIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(empty));
        administration.FetchStoreIndexAsync(Arg.Is<PluginStoreConfig>(store => store.Location == "https://slow.example/index.json"), Arg.Any<CancellationToken>()).Returns(slow.Task);
        administration.FetchStoreIndexAsync(Arg.Is<PluginStoreConfig>(store => store.Location == "https://failed.example/index.json"), Arg.Any<CancellationToken>()).Returns(Task.FromResult(new PluginStoreFetchResult(false, "Unavailable", null, null)));
        administration.FetchStoreIndexAsync(Arg.Is<PluginStoreConfig>(store => store.Signed != null && store.Signed.SourceKey == sourceKey), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                workingStarted.TrySetResult();
                return Task.FromResult(new PluginStoreFetchResult(true, null, new PluginStoreIndex("Private tools", [new("tool", "Tool", null, null, "1.0.0", [])]), "https://private.example/index.json"));
            });
        var manager = new PluginManagerViewModel(administration, Substitute.For<IPluginRegistrationStore>(),
            Substitute.For<ISessionDialogService>(), configStore, Substitute.For<IPluginStoreClient>(), new Dictionary<string, PluginSettingsRegistration>(), storeSources: sources);
        await manager.LoadAsync();
        Assert.Equal(4, manager.Stores.Count);
        var privateRow = Assert.Single(manager.StoreInfos, info => info.Store.Signed?.SourceKey == sourceKey);
        Assert.Equal("Private tools", privateRow.Name);
        Assert.False(privateRow.CanRemove);
        await manager.RemoveStoreCommand.ExecuteAsync(privateRow);
        Assert.Equal(configured, Assert.Single(await configStore.LoadAsync()));
        using var visiblePlugin = new VisiblePluginObserver(manager.AvailablePlugins, "tool");
        var browsing = manager.BrowseStoresAsync();
        try
        {
            await workingStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await visiblePlugin.Visible.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("tool", Assert.Single(manager.AvailablePlugins).Entry.Id);
            Assert.False(browsing.IsCompleted);
        }
        finally
        {
            slow.SetResult(empty);
            await browsing;
        }
        Assert.Equal("tool", Assert.Single(manager.AvailablePlugins).Entry.Id);

        var cockpit = TestCockpit.NewViewModel();
        var storeClient = Substitute.For<IPluginStoreClient>();
        storeClient.FetchIndexAsync(Arg.Any<PluginStoreConfig>(), Arg.Any<CancellationToken>())
            .Returns(call => administration.FetchStoreIndexAsync(call.Arg<PluginStoreConfig>(), call.Arg<CancellationToken>()));
        var updateCount = 0;
        var checker = new PluginUpdateChecker(_ => Task.FromResult<IReadOnlyList<DiscoveredPlugin>>([
                new(_directory, "tool", new PluginManifest("tool", "Tool", "0.9.0", null, 3, null, null, null, null), "installed", PluginLoadDecision.Load),
            ]), configStore, storeClient, Substitute.For<IToastService>(), cockpit, NullLogger<PluginUpdateChecker>.Instance,
            isUpdateStaged: (_, _) => false, storeSources: sources, setUpdateBadgeCount: count => updateCount = count);
        await checker.CheckNowAsync();
        Assert.Equal(1, updateCount);

        await manager.ToggleStoreHiddenCommand.ExecuteAsync(privateRow);
        await manager.LoadAsync();
        Assert.DoesNotContain(manager.Stores, store => store.Signed?.SourceKey == sourceKey);
        var hiddenRow = Assert.Single(manager.StoreInfos, info => info.Store.Signed?.SourceKey == sourceKey);
        Assert.True(hiddenRow.IsHidden);
        await checker.CheckNowAsync();
        Assert.Equal(0, updateCount);
        await manager.ToggleStoreHiddenCommand.ExecuteAsync(hiddenRow);
        await manager.LoadAsync();
        Assert.Equal(4, manager.Stores.Count);
        await checker.CheckNowAsync();
        Assert.Equal(1, updateCount);

        registration.Dispose();
        await manager.LoadAsync();
        Assert.Equal(configured, Assert.Single(manager.Stores));
        await checker.CheckNowAsync();
        Assert.Equal(0, updateCount);
        var beforeDetachedChange = notifications;
        provider.Change([]);
        Assert.Equal(beforeDetachedChange, notifications);
        Assert.Equal(configured, Assert.Single(await configStore.LoadAsync()));
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class VisiblePluginObserver : IDisposable
    {
        private readonly ObservableCollection<StorePluginRowViewModel> _rows;
        private readonly string _id;
        private readonly TaskCompletionSource _visible = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public VisiblePluginObserver(ObservableCollection<StorePluginRowViewModel> rows, string id)
        {
            _rows = rows;
            _id = id;
            _rows.CollectionChanged += _OnCollectionChanged;
        }

        public Task Visible => _visible.Task;

        private void _OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (_rows.Any(row => row.Id == _id))
            {
                _visible.TrySetResult();
            }
        }

        public void Dispose() => _rows.CollectionChanged -= _OnCollectionChanged;
    }

    private sealed class MutableProvider : IPluginStoreSourceProvider
    {
        public IReadOnlyList<PluginStoreSourceDescriptor> Sources { get; private set; } = [];

        public event EventHandler? SourcesChanged;

        public void Change(IReadOnlyList<PluginStoreSourceDescriptor> sources)
        {
            Sources = sources;
            SourcesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class FailingProvider : IPluginStoreSourceProvider
    {
        public IReadOnlyList<PluginStoreSourceDescriptor> Sources => throw new InvalidOperationException("The provider is unavailable.");

        public event EventHandler? SourcesChanged
        {
            add { }
            remove { }
        }
    }
}

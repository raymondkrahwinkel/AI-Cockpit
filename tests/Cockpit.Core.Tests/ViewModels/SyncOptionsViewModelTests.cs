using Cockpit.App.ViewModels;
using Cockpit.Core.Configuration;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1846: what Options › Sync hands to <see cref="ISyncOptions.SaveAsync"/>. A category nobody is ticked for must still
/// be written, as an empty list: a missing <c>SettingsSync.Categories</c> is migrated again at the next start, which
/// would send everything to the old server after all.
/// </summary>
public class SyncOptionsViewModelTests
{
    private static readonly string[] Categories = ["Profiles", "Plugins", "Notifications", "Shortcuts", "SessionBehaviour", "Usage", "Appearance", "Updates"];

    [Fact]
    public async Task SaveStagedAsync_WritesEveryCategoryAsAList_AlsoAnEmptyOne()
    {
        var source = new FakeSyncOptions();
        var viewModel = new SyncOptionsViewModel(source);
        await viewModel.LoadAsync();

        viewModel.Rows.Single(row => row.Id == "Notifications").Servers[0].IsTicked = false;
        viewModel.Rows.Single(row => row.Id == "Appearance").Servers[1].IsTicked = true;
        await viewModel.SaveStagedAsync();

        Assert.Equal(8, source.Saved?.Count);
        Assert.Empty(source.Saved!["Notifications"]);
        Assert.Equal(["home", "work"], source.Saved["Appearance"]);
        Assert.Equal(["home"], source.Saved["Plugins"]);
    }

    private sealed class FakeSyncOptions : ISyncOptions
    {
        public IReadOnlyDictionary<string, IReadOnlyList<string>>? Saved { get; private set; }

        public Task<SyncOverview> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SyncOverview(
            [new SyncServer("home", "Depot: Home", "home.example", true), new SyncServer("work", "Depot: Work", "work.example", true)],
            Categories.ToDictionary(category => category, _ => (IReadOnlyList<string>)["home"]),
            0,
            true,
            ["home", "work"],
            new Dictionary<string, SyncStanding>()));

        public Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, IReadOnlyList<string> seenServers, CancellationToken cancellationToken = default)
        {
            Saved = categories;
            return Task.CompletedTask;
        }
    }
}

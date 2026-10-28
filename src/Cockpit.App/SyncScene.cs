using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Configuration;

namespace Cockpit.App;

// AC-1846: Options › Sync over a stand-in source with three servers (one signed out), so the render shows the rows, the notes and the cards.
internal static class SyncScene
{
    private static readonly string[] AllCategories = ["Profiles", "Plugins", "Notifications", "Shortcuts", "SessionBehaviour", "Usage", "Appearance", "Updates"];

    // An existing install that has not changed a choice yet: everything goes to Home, and the banner says so.
    public static OptionsDialog Unchanged(int width, int height) => _Dialog(width, height, reviewed: false);

    // The same page after a few edits; they are staged once the dialog is up, because opening it reloads the saved choices.
    public static OptionsDialog Changed(int width, int height) => _Dialog(width, height, reviewed: true);

    // Notifications switched off for Home, Appearance, Usage and Updates also on Work: the inline notes.
    public static void StageChanges(Avalonia.Controls.Window window)
    {
        var sync = ((CockpitViewModel)window.DataContext!).Sync;
        _Tick(sync, "Notifications", "home", false);
        _Tick(sync, "Appearance", "work", true);
        _Tick(sync, "Usage", "work", true);
        _Tick(sync, "Updates", "work", true);
    }

    // The settings choice with two servers that disagree about one key (screen 7).
    public static SettingsConflictDialog Disagree(int width, int height) => new()
    {
        DataContext = new SettingsConflictViewModel(
        [
            new SettingsConflict("Terminal.FontSize", "15", "14", new Dictionary<string, string?> { ["Depot: Home"] = "14", ["Depot: Work"] = "16" }),
        ]),
    };

    private static void _Tick(SyncOptionsViewModel sync, string category, string server, bool ticked) =>
        sync.Rows.First(row => row.Id == category).Servers.First(tick => tick.Server.Id == server).IsTicked = ticked;

    private static OptionsDialog _Dialog(int width, int height, bool reviewed)
    {
        var cockpit = new CockpitViewModel { Sync = new SyncOptionsViewModel(new SceneSource(reviewed)) };
        cockpit.Sync.LoadCommand.Execute(null);
        var dialog = new OptionsDialog { DataContext = cockpit, Width = width, Height = height };
        dialog.SelectCategory("sync");
        return dialog;
    }

    private sealed class SceneSource(bool reviewed) : ISyncOptions
    {
        public Task<SyncOverview> ReadAsync(CancellationToken cancellationToken = default)
        {
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SyncOverview(
                [
                    new SyncServer("home", "Depot: Home", "depot.krahwinkel-it.nl", true),
                    new SyncServer("work", "Depot: Work", "depot.client.example", true),
                    new SyncServer("lab", "Depot: Lab", "lab.example.net", false),
                ],
                AllCategories.ToDictionary(category => category, _ => (IReadOnlyList<string>)["home"]),
                5,
                reviewed,
                reviewed ? ["home", "work", "lab"] : ["home"],
                new Dictionary<string, SyncStanding>
                {
                    ["home"] = new(now.AddMinutes(-2), 14 * 1024),
                    ["work"] = new(now.AddMinutes(-2), 6 * 1024),
                }));
        }

        public Task SaveAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> categories, IReadOnlyList<string> seenServers, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

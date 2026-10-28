using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Configuration;

namespace Cockpit.App.ViewModels;

// AC-1846: Options › Sync. Which servers each category of settings is kept in step with; staged, written on Apply.
public sealed partial class SyncOptionsViewModel(ISyncOptions? source = null, TimeProvider? clock = null) : ObservableObject
{
    // The ids are the ones `SettingsCarrierFilter` knows; the words are the operator's.
    private static readonly (string Id, string Title, string Description)[] Catalogue =
    [
        ("Profiles", "Profiles", "Label, provider, session defaults, delegation, enabled MCP servers"),
        ("Plugins", "Plugins", "Which are on, menu order, hidden, pinned to the sidebar"),
        ("Notifications", "Notifications", "Which events notify you, idle thresholds"),
        ("Shortcuts", "Shortcuts", "Key bindings, the session-switching modifier"),
        ("SessionBehaviour", "Session behaviour", "Auto-close, queued messages, wake agents, memory budget, timestamps"),
        ("Usage", "Usage", "Warning thresholds per provider, profile and assistant; usage pill fields"),
        ("Appearance", "Appearance", "Theme, terminal font, projects layout"),
        ("Updates", "Updates", "Release channel, check on startup"),
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private SyncOverview? _loaded;
    private string _savedFingerprint = string.Empty;

    public ObservableCollection<SyncCategoryRowViewModel> Rows { get; } = [];

    // Inline notes about what a pending change does; nothing here is ever deleted from a server.
    public ObservableCollection<SyncNote> Notes { get; } = [];

    public ObservableCollection<SyncServerCardViewModel> Cards { get; } = [];

    public ObservableCollection<SyncOfferViewModel> Offers { get; } = [];

    // The Backup row is read-only: what Options › Backup chose, said here so the list reads complete.
    [ObservableProperty]
    private string _backupTargetText = "Chosen in Options › Backup";

    [ObservableProperty]
    private string _bannerServers = string.Empty;

    public bool ShowBanner => BannerServers.Length > 0;

    public bool HasNotes => Notes.Count > 0;

    public bool HasOffers => Offers.Count > 0;

    // Empty while the choices are as saved; what the Options footer's pending-changes indicator compares.
    public string Fingerprint => _Fingerprint() == _savedFingerprint ? string.Empty : "changed:" + _Fingerprint();

    partial void OnBannerServersChanged(string value) => OnPropertyChanged(nameof(ShowBanner));

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (source is null)
        {
            return;
        }

        _Show(await source.ReadAsync().ConfigureAwait(true));
    }

    public async Task SaveStagedAsync()
    {
        if (source is null || _loaded is null || Fingerprint.Length == 0)
        {
            return;
        }

        var categories = Rows.Where(row => row.Servers.Any(tick => tick.IsTicked)).ToDictionary(
            row => row.Id,
            row => (IReadOnlyList<string>)[.. row.Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id)],
            StringComparer.Ordinal);
        await source.SaveAsync(categories, [.. _loaded.Servers.Select(server => server.Id).Union(_loaded.SeenServers)]).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    private void _Show(SyncOverview overview)
    {
        _loaded = overview;
        foreach (var row in Rows)
        {
            row.Changed = null;
        }

        Rows.Clear();
        foreach (var (id, title, description) in Catalogue)
        {
            var ticked = overview.Categories?.GetValueOrDefault(id) ?? [];
            var row = new SyncCategoryRowViewModel(id, title, description, [.. overview.Servers.Select(server => new SyncServerTickViewModel(server, ticked.Contains(server.Id), !overview.SeenServers.Contains(server.Id)))]);
            row.Changed = _Refresh;
            Rows.Add(row);
        }

        _savedFingerprint = _Fingerprint();
        BannerServers = overview.Categories is null || overview.Reviewed
            ? string.Empty
            : string.Join(" + ", overview.Servers.Where(server => Rows.All(row => row.Servers.First(tick => tick.Server.Id == server.Id).IsTicked)).Select(server => server.Name));
        _Refresh();
    }

    private void _Refresh()
    {
        Notes.Clear();
        foreach (var row in Rows)
        {
            var before = _loaded?.Categories?.GetValueOrDefault(row.Id) ?? [];
            foreach (var tick in row.Servers.Where(tick => !tick.IsTicked && before.Contains(tick.Server.Id)))
            {
                Notes.Add(new SyncNote($"{row.Title}: turned off for {tick.Server.Name}.", $"This cockpit stops sending and reading it. The copy already on {tick.Server.Name} stays there, untouched" + (row.Servers.Any(other => other.IsTicked) ? "; other ticked servers keep syncing" : string.Empty) + " — remove it in the server if you want it gone."));
            }

            foreach (var tick in row.Servers.Where(tick => tick.IsTicked && !before.Contains(tick.Server.Id) && row.Servers.Any(other => other.IsTicked && other != tick)))
            {
                Notes.Add(new SyncNote($"{row.Title}: adding {tick.Server.Name}.", $"{tick.Server.Name} becomes another partner next to the others. Changes go to all of them; each keeps its own last-seen state. If {tick.Server.Name} already holds {row.Title}, the first sync asks which copy wins."));
            }
        }

        Offers.Clear();
        foreach (var server in _loaded?.Servers.Where(server => server.SignedIn && !_loaded.SeenServers.Contains(server.Id) && Rows.All(row => !row.Servers.First(tick => tick.Server.Id == server.Id).IsTicked)) ?? [])
        {
            Offers.Add(new SyncOfferViewModel(server, () => _TickEverything(server)));
        }

        _RebuildCards();
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasOffers));
        OnPropertyChanged(nameof(Fingerprint));
    }

    private void _TickEverything(SyncServer server)
    {
        foreach (var row in Rows)
        {
            row.Servers.First(tick => tick.Server.Id == server.Id).IsTicked = true;
        }
    }

    private void _RebuildCards()
    {
        Cards.Clear();
        var standing = _loaded?.Standing ?? new Dictionary<string, SyncStanding>();
        foreach (var server in _loaded?.Servers ?? [])
        {
            var mine = Rows.Where(row => row.Servers.First(tick => tick.Server.Id == server.Id).IsTicked).ToList();
            var lines = mine.Select(row => new SyncCardLine(
                row.Title,
                "all" + string.Concat(row.Servers.Where(tick => tick.IsTicked && tick.Server.Id != server.Id).Select(tick => $" · also on {SyncCategoryRowViewModel.ShortName(tick.Server.Name)}")))).ToList();
            Cards.Add(new SyncServerCardViewModel(
                server,
                lines,
                server.SignedIn ? $"carries {mine.Count} of {Rows.Count}" : "signed out",
                standing.TryGetValue(server.Id, out var last) ? $"Last synced {_Ago(last.At)} · {_Size(last.Bytes)}" : server.SignedIn ? "Not synced yet in this session" : string.Empty));
        }
    }

    private string _Ago(DateTimeOffset at)
    {
        var age = _clock.GetUtcNow() - at;
        return age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago" : age.TotalDays < 1 ? $"{(int)age.TotalHours} hours ago" : $"{(int)age.TotalDays} days ago";
    }

    private static string _Size(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024} KB";

    private string _Fingerprint() => string.Join("|", Rows.Select(row => row.Id + "=" + string.Join(",", row.Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id))));
}

public sealed record SyncNote(string Title, string Text);

public sealed record SyncCardLine(string Title, string Detail);

public sealed class SyncOfferViewModel(SyncServer server, Action accept)
{
    public string Text => $"{server.Name} is new here. Sync everything with it?";

    public IRelayCommand AcceptCommand { get; } = new RelayCommand(accept);
}

public sealed record SyncServerCardViewModel(SyncServer Server, IReadOnlyList<SyncCardLine> Lines, string Pill, string Standing)
{
    public bool IsSignedOut => !Server.SignedIn;

    public double Opacity => IsSignedOut ? 0.6 : 1.0;

    public bool HasLines => Lines.Count > 0;

    public bool HasStanding => Standing.Length > 0;

    public string SignedOutHint => "Receives nothing. Sign in under Depot settings to choose it above.";
}

public sealed class SyncCategoryRowViewModel : ObservableObject
{
    public SyncCategoryRowViewModel(string id, string title, string description, IReadOnlyList<SyncServerTickViewModel> servers)
    {
        (Id, Title, Description, Servers) = (id, title, description, servers);
        foreach (var tick in servers)
        {
            tick.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(Summary));
                Changed?.Invoke();
            };
        }
    }

    public string Id { get; }

    public string Title { get; }

    public string Description { get; }

    public IReadOnlyList<SyncServerTickViewModel> Servers { get; }

    public Action? Changed { get; set; }

    // "Depot: Home" for one, "Home + Work" for several, "Not synced" for none.
    public string Summary => Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Name).ToList() switch
    {
        [] => "Not synced",
        [var only] => only,
        var many => string.Join(" + ", many.Select(ShortName)),
    };

    internal static string ShortName(string name) => name.Contains(": ", StringComparison.Ordinal) ? name[(name.IndexOf(": ", StringComparison.Ordinal) + 2)..] : name;
}

public sealed partial class SyncServerTickViewModel(SyncServer server, bool isTicked, bool isNew) : ObservableObject
{
    public SyncServer Server { get; } = server;

    [ObservableProperty]
    private bool _isTicked = isTicked;

    // Signed out: listed, not tickable; a tick it already has can still be taken away.
    public bool IsEnabled => Server.SignedIn || IsTicked;

    public string Tag => !Server.SignedIn ? "sign in first" : isNew ? "new" : string.Empty;

    partial void OnIsTickedChanged(bool value) => OnPropertyChanged(nameof(IsEnabled));
}

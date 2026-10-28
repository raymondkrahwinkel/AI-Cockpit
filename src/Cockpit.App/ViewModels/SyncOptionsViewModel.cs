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
    private readonly Dictionary<EditableProfileViewModel, SyncProfileRowViewModel> _profileRows = [];
    private ManageProfilesDialogViewModel? _profiles;
    private SyncOverview? _loaded;
    private string _savedFingerprint = string.Empty;
    private bool _touched;

    public ObservableCollection<SyncCategoryRowViewModel> Rows { get; } = [];

    // AC-1847: one row per profile, the same choice the profile page shows; listed under the Profiles row when it is opened.
    public ObservableCollection<SyncProfileRowViewModel> ProfileRows { get; } = [];

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

    // The profile list the Options › Profiles page edits; a row follows each profile, new, renamed or removed.
    public void AttachProfiles(ManageProfilesDialogViewModel profiles)
    {
        _profiles = profiles;
        profiles.Profiles.CollectionChanged += (_, _) => _RebuildProfileRows();
        _RebuildProfileRows();
    }

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

        var categories = Rows.ToDictionary(
            row => row.Id,
            row => (IReadOnlyList<string>)[.. row.Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id)],
            StringComparer.Ordinal);
        var overrides = ProfileRows
            .Where(row => row.IsOverridden && row.Label.Length > 0)
            .GroupBy(row => row.Label, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Last().TickedIds], StringComparer.Ordinal);
        await source.SaveAsync(categories, overrides, [.. _loaded.Servers.Select(server => server.Id).Union(_loaded.SeenServers)]).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    private void _Show(SyncOverview overview)
    {
        _loaded = overview;
        _touched = false;
        foreach (var row in Rows)
        {
            row.Changed = null;
        }

        _profileRows.Clear();

        Rows.Clear();
        foreach (var (id, title, description) in Catalogue)
        {
            var ticked = overview.Categories?.GetValueOrDefault(id) ?? [];
            var row = new SyncCategoryRowViewModel(id, title, description, [.. overview.Servers.Select(server => new SyncServerTickViewModel(server, ticked.Contains(server.Id), !overview.SeenServers.Contains(server.Id)))]);
            row.Changed = _CategoryChanged;
            row.Children = id == "Profiles" ? ProfileRows : null;
            Rows.Add(row);
        }

        _RebuildProfileRows();
        _savedFingerprint = _Fingerprint();
        BannerServers = overview.Categories is null || overview.Reviewed
            ? string.Empty
            : string.Join(" + ", overview.Servers.Where(server => Rows.All(row => row.Servers.First(tick => tick.Server.Id == server.Id).IsTicked)).Select(server => server.Name));
        _Refresh();
    }

    private void _CategoryChanged()
    {
        _touched = true;
        foreach (var row in ProfileRows)
        {
            row.FollowCategory();
        }

        _Refresh();
    }

    // The rows follow the profile list; what a profile was saved with is looked up by the label it had when the page opened.
    private void _RebuildProfileRows()
    {
        ProfileRows.Clear();
        var profilesRow = Rows.FirstOrDefault(row => row.Id == "Profiles");
        if (_loaded is null || _profiles is null || profilesRow is null)
        {
            return;
        }

        foreach (var profile in _profiles.Profiles)
        {
            if (!_profileRows.TryGetValue(profile, out var row))
            {
                var saved = _loaded.ProfileOverrides.GetValueOrDefault(profile.OriginalLabel ?? profile.Label);
                row = new SyncProfileRowViewModel(
                    profile,
                    [.. _loaded.Servers.Select(server => new SyncServerTickViewModel(server, saved?.Contains(server.Id) ?? profilesRow.Servers.First(tick => tick.Server.Id == server.Id).IsTicked, isNew: false))],
                    profilesRow,
                    () =>
                    {
                        _touched = true;
                        _Refresh();
                    });
                _profileRows[profile] = row;
            }

            ProfileRows.Add(row);
        }

        foreach (var gone in _profileRows.Keys.Where(profile => !_profiles.Profiles.Contains(profile)).ToList())
        {
            _profileRows.Remove(gone);
        }

        if (!_touched)
        {
            _savedFingerprint = _Fingerprint();
        }

        _Refresh();
    }

    private void _Refresh()
    {
        _RefreshOverriddenText();
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

    private void _RefreshOverriddenText()
    {
        var count = ProfileRows.Count(row => row.IsOverridden);
        if (Rows.FirstOrDefault(row => row.Id == "Profiles") is { } row)
        {
            row.OverriddenText = count == 0 ? string.Empty : $"{count} overridden";
        }
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

    private string _Fingerprint() => string.Join("|", Rows.Select(row => row.Id + "=" + string.Join(",", row.Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id))))
        + "|" + string.Join("|", ProfileRows.Where(row => row.IsOverridden).Select(row => row.Label + "=" + string.Join(",", row.TickedIds)));
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
    private bool _isExpanded;
    private string _overriddenText = string.Empty;

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

    // The Profiles row only: the per-profile rows listed under it when it is opened (AC-1847).
    public IReadOnlyList<SyncProfileRowViewModel>? Children { get; set; }

    public bool IsExpandable => Children is not null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string OverriddenText
    {
        get => _overriddenText;
        set
        {
            if (SetProperty(ref _overriddenText, value))
            {
                OnPropertyChanged(nameof(HasOverrides));
            }
        }
    }

    public bool HasOverrides => _overriddenText.Length > 0;

    public IRelayCommand ToggleCommand => new RelayCommand(() => IsExpanded = !IsExpanded);

    // "Depot: Home" for one, "Home + Work" for several, "Not synced" for none.
    public string Summary => Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Name).ToList() switch
    {
        [] => "Not synced",
        [var only] => only,
        var many => string.Join(" + ", many.Select(ShortName)),
    };

    public IReadOnlyList<string> TickedIds => [.. Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id)];

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

// AC-1847: one profile's choice. It shows the servers the profile goes to; equal to the Profiles category means "same as Profiles", anything else overrides it.
public sealed class SyncProfileRowViewModel : ObservableObject
{
    private readonly SyncCategoryRowViewModel _category;
    private readonly Action _changed;
    private bool _applying;
    private bool _follows;

    public SyncProfileRowViewModel(EditableProfileViewModel profile, IReadOnlyList<SyncServerTickViewModel> servers, SyncCategoryRowViewModel category, Action changed)
    {
        (Profile, Servers, _category, _changed) = (profile, servers, category, changed);
        _follows = _SameAsCategory();
        foreach (var tick in servers)
        {
            tick.PropertyChanged += (_, e) =>
            {
                if (!_applying && e.PropertyName == nameof(SyncServerTickViewModel.IsTicked))
                {
                    _follows = _SameAsCategory();
                    _Raise();
                    _changed();
                }
            };
        }

        profile.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Subtitle));
        };
        profile.SyncChoice = this;
    }

    public EditableProfileViewModel Profile { get; }

    public IReadOnlyList<SyncServerTickViewModel> Servers { get; }

    public string Label => Profile.Label ?? string.Empty;

    public string Subtitle => string.Join(" · ", new[] { Profile.SelectedProvider?.Label, Profile.Purpose }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public IReadOnlyList<string> TickedIds => [.. Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Id)];

    public bool IsOverridden => !_follows;

    public string InheritedText => _category.Summary;

    public string Hint => $"Tick one or more servers; the profile appears on your other machines via each. Nothing ticked = not synced. “Same as Profiles” (now: {_category.Summary}) is the default and follows Options › Sync › Profiles. Unticking a server deletes nothing on the server.";

    public string Summary => _follows ? $"Same as Profiles · {_category.Summary}" : _Names(capitalised: true);

    // The small tag in the profile list: nothing while it follows Profiles.
    public string ListPill => _follows ? string.Empty : _Names(capitalised: false);

    public IRelayCommand UseCategoryCommand => new RelayCommand(() =>
    {
        _Copy();
        _follows = true;
        _Raise();
        _changed();
    });

    // The Profiles category moved: a profile that followed it moves along, one that deviated may now match it again.
    public void FollowCategory()
    {
        if (_follows)
        {
            _Copy();
        }
        else
        {
            _follows = _SameAsCategory();
        }

        _Raise();
    }

    private string _Names(bool capitalised)
    {
        var ticked = Servers.Where(tick => tick.IsTicked).Select(tick => tick.Server.Name).ToList();
        return ticked.Count == 0 ? (capitalised ? "Not synced" : "not synced") : string.Join(" + ", ticked.Select(SyncCategoryRowViewModel.ShortName));
    }

    private bool _SameAsCategory() => TickedIds.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(_category.TickedIds.OrderBy(id => id, StringComparer.Ordinal));

    private void _Copy()
    {
        _applying = true;
        foreach (var tick in Servers)
        {
            tick.IsTicked = _category.TickedIds.Contains(tick.Server.Id);
        }

        _applying = false;
    }

    private void _Raise()
    {
        OnPropertyChanged(nameof(IsOverridden));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ListPill));
        OnPropertyChanged(nameof(InheritedText));
        OnPropertyChanged(nameof(Hint));
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// Mirrors the `ProjectInfoFieldViewModel` idiom: a row the operator adds and can leave alone, dropped on save rather
// than held against them (`IsBlank`) (AC-485, AC-165).
public partial class ProjectResourceRowViewModel : ViewModelBase
{
    // Every role the picker offers, in the order `ProjectResourceRole` declares them.
    public static IReadOnlyList<ProjectResourceRole> RoleChoices { get; } = Enum.GetValues<ProjectResourceRole>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsMemorySourcePicker))]
    [NotifyPropertyChangedFor(nameof(IsMemoryFolderMode))]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    [NotifyPropertyChangedFor(nameof(ShowsSendsContentOption))]
    [NotifyPropertyChangedFor(nameof(ShowsMemorySourceServerRow))]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    [NotifyPropertyChangedFor(nameof(SecretPathWarning))]
    private ProjectResourceRole _role;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    [NotifyPropertyChangedFor(nameof(IsSecretPath))]
    [NotifyPropertyChangedFor(nameof(SecretPathWarning))]
    [NotifyPropertyChangedFor(nameof(ShowsScopeLabel))]
    [NotifyPropertyChangedFor(nameof(HasRepoRelativeFix))]
    private string _reference;

    [ObservableProperty]
    private string _label;

    // Defaults to true — the opposite default from `ProjectInfoFieldViewModel.IsSharedWithSessions`, deliberately: an
    // information row arrives as reference material for the operator first, while a memory or instruction row exists
    // specifically to be told to the session that reads or obeys it (AC-483).
    [ObservableProperty]
    private bool _reachesSessions;

    // Whether this row's contents travel with a starting session rather than only its location (AC-486) — see
    // `ProjectResource.SendsContent`'s own doc comment for why this defaults off and is offered for
    // `ProjectResourceRole.Instructions` alone.
    [ObservableProperty]
    private bool _sendsContent;

    // `MemorySourceChoices` is the same list for every row in the dialog — the registry does not change per row, only
    // which entry each row picked (AC-166, AC-499).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryFolderMode))]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    [NotifyPropertyChangedFor(nameof(FamilyInstanceChoices))]
    [NotifyPropertyChangedFor(nameof(HasFamilyInstances))]
    [NotifyPropertyChangedFor(nameof(ShowsMemorySourceServerRow))]
    [NotifyPropertyChangedFor(nameof(CanConfigureMemorySource))]
    [NotifyPropertyChangedFor(nameof(MemorySourceInstanceEmptyHint))]
    private MemorySourceChoice? _selectedMemorySourceChoice;

    // Which instance of `SelectedMemorySourceChoice`'s family this row picked (AC-499) — "Depot (krahwinkel-it)" out of
    // however many connections that family currently has.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    private MemorySourceChoice? _selectedFamilyInstance;

    // Disables the button rather than hiding it (`CanConfigureMemorySource` stays about whether a configure action
    // exists at all, never about whether one is currently running) so a slow or hung
    // `ProjectMemorySourceFamily.ConfigureAsync` reads as "working", not as the button vanishing mid-click (AC-499).
    [ObservableProperty]
    private bool _isConfiguringMemorySource;

    // Cleared at the start of every call so a stale failure never survives a successful retry (AC-499).
    [ObservableProperty]
    private string? _memorySourceConfigureError;

    // Whether this reference could not be found (AC-485), set from outside.
    [ObservableProperty]
    private bool _isBroken;

    // Renamed from the old `IsMachineBound` bool (AC-605 criterion 6): a property that only ever answered
    // "machine-bound or not" could not tell an anchor-relative reference (travels to any machine the operator opens the
    // project on) apart from a repo-relative one (travels with the repo).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsScopeLabel))]
    private ProjectResourceScope? _scope;

    // The repo-relative form this row's `Reference` should have been saved as, when it is a fully qualified path that
    // already lives inside the project's own folder but was never actually converted (AC-605 criterion 5) — hand-typed,
    // or written by hand into `cockpit.json`.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepoRelativeFix))]
    [NotifyPropertyChangedFor(nameof(ShowsScopeLabel))]
    private string? _repoRelativeFix;

    // What a Memory row's own registered source found about the typed value (AC-503), or null when nothing is known
    // yet.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    private ProjectMemorySourceReachability? _reachability;

    // The text to show under a `ProjectMemorySourceReachability.Confirmed` or (AC-499)
    // `ProjectMemorySourceReachability.CheckFailed` row — the plugin's own
    // `ProjectMemorySourceReachabilityResult.Detail`, or null to fall back to each state's own fixed sentence.
    [ObservableProperty]
    private string? _reachabilityDetail;

    // Gates the row's own bottom divider in the view: the last row has no next row to separate itself from, so it must
    // draw no line at all (AC-485).
    [ObservableProperty]
    private bool _isLastRow;

    // The choices offered for this row's memory source — the same list, shared, for every row (AC-166).
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; }

    // Replacing this dictionary after server settings change prevents rows from keeping stale family instances
    // (AC-499, AC-523).
    private IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>> _familyInstanceChoicesByKey;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>> _EmptyFamilyInstanceChoices =
        new Dictionary<string, IReadOnlyList<MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase);

    public ProjectResourceRowViewModel(
        ObservableCollection<MemorySourceChoice> memorySourceChoices,
        ProjectResourceRole role = ProjectResourceRole.Memory,
        string reference = "",
        string label = "",
        bool reachesSessions = true,
        bool sendsContent = false,
        IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>>? familyInstanceChoicesByKey = null)
    {
        MemorySourceChoices = memorySourceChoices;
        _familyInstanceChoicesByKey = familyInstanceChoicesByKey ?? _EmptyFamilyInstanceChoices;
        _role = role;
        _reference = reference;
        _label = label;
        _reachesSessions = reachesSessions;
        // AC-612: a row loaded already ticked for a secret-shaped reference (a hand-edited cockpit.json, or one saved
        // before this ticket existed) must never arrive checked.
        _sendsContent = sendsContent && !ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference);
    }

    // Swaps in a freshly rebuilt family-instances dictionary (AC-523).
    internal void UpdateFamilyInstanceChoices(IReadOnlyDictionary<string, IReadOnlyList<MemorySourceChoice>> familyInstanceChoicesByKey)
    {
        _familyInstanceChoicesByKey = familyInstanceChoicesByKey;

        if (SelectedFamilyInstance is { Scheme: { } scheme })
        {
            SelectedFamilyInstance = FamilyInstanceChoices.FirstOrDefault(choice =>
                string.Equals(choice.Scheme, scheme, StringComparison.OrdinalIgnoreCase));
        }

        OnPropertyChanged(nameof(FamilyInstanceChoices));
        OnPropertyChanged(nameof(HasFamilyInstances));
        OnPropertyChanged(nameof(MemorySourceInstanceEmptyHint));
    }

    // Keeps a role switch from silently changing what `Reference` means (AC-485 review, MUST-FIX 1).
    partial void OnRoleChanged(ProjectResourceRole oldValue, ProjectResourceRole newValue)
    {
        // Always clear reachability because changing roles invalidates the previous source result (AC-503).
        _ResetReachability();

        // AC-486: leaving Instructions must not leave "Send along" quietly ticked on a row where it now means nothing —
        // the checkbox is about to disappear (see ShowsSendsContentOption below), and nothing reads this flag for any
        // other role.
        if (oldValue == ProjectResourceRole.Instructions && newValue != ProjectResourceRole.Instructions)
        {
            SendsContent = false;
        }

        if (oldValue == ProjectResourceRole.Memory && newValue != ProjectResourceRole.Memory)
        {
            if (SelectedMemorySourceLeaf is { Scheme.Length: > 0 } leaf)
            {
                var typed = Reference.Trim();
                Reference = typed.Length > 0 ? $"{leaf.Scheme}:{typed}" : string.Empty;
            }

            SelectedMemorySourceChoice = MemorySourceChoices.Count > 0 ? MemorySourceChoices[0] : null;
            return;
        }

        if (newValue == ProjectResourceRole.Memory && oldValue != ProjectResourceRole.Memory)
        {
            if (MemorySourceChoices.Count > 0
                && ProjectMemoryRef.TryParse(Reference, out var scheme, out var value)
                && TryMatchMemorySourceScheme(scheme, out var top, out var instance))
            {
                SelectedMemorySourceChoice = top;
                SelectedFamilyInstance = instance;
                Reference = value;
            }
            else if (MemorySourceChoices.Count > 0)
            {
                SelectedMemorySourceChoice = MemorySourceChoices[0];
            }
        }
    }

    // AC-612: also the live half of the secret-path guard the constructor applies at load (AC-503).
    partial void OnReferenceChanged(string value)
    {
        _ResetReachability();

        if (IsSecretPath)
        {
            SendsContent = false;
        }
    }

    // AC-612: the belt to `OnReferenceChanged`'s braces.
    partial void OnSendsContentChanged(bool value)
    {
        if (value && IsSecretPath)
        {
            SendsContent = false;
        }
    }

    // AC-499: also resets the instance axis to the newly picked family's own first instance (or null, if it has none,
    // or the new top choice is not a family at all) — a stale instance pointing at the previous family's own
    // connections must not survive the top choice changing out from under it (AC-503).
    partial void OnSelectedMemorySourceChoiceChanged(MemorySourceChoice? value)
    {
        _ResetReachability();
        SelectedFamilyInstance = value?.FamilyKey is { } familyKey
            && _familyInstanceChoicesByKey.TryGetValue(familyKey, out var instances)
            && instances.Count > 0
                ? instances[0]
                : null;

        // AC-499: a "Servers…" failure belongs to the family it was raised for — switching away from it (or off
        // Memory entirely) must not leave that message showing under a server row that now names something else.
        MemorySourceConfigureError = null;
    }

    // AC-503/AC-499: a Reachability answer belongs to a specific instance too — switching within a family invalidates whatever the previous instance found, the same reason switching the top choice does.
    partial void OnSelectedFamilyInstanceChanged(MemorySourceChoice? value) => _ResetReachability();

    private void _ResetReachability()
    {
        Reachability = null;
        ReachabilityDetail = null;
    }

    // Finds which top-level choice and, if it is a family, which of its instances a saved `scheme` names (AC-499) — one
    // layer deeper than the pre-AC-499 search that only ever looked at `MemorySourceChoices` itself.
    internal bool TryMatchMemorySourceScheme(string scheme, out MemorySourceChoice? top, out MemorySourceChoice? instance)
    {
        var ungrouped = MemorySourceChoices.FirstOrDefault(choice =>
            choice.FamilyKey is null && choice.Scheme is { } candidate && string.Equals(candidate, scheme, StringComparison.OrdinalIgnoreCase));
        if (ungrouped is not null)
        {
            top = ungrouped;
            instance = null;
            return true;
        }

        foreach (var (familyKey, instances) in _familyInstanceChoicesByKey)
        {
            var matchedInstance = instances.FirstOrDefault(choice =>
                choice.Scheme is { } candidate && string.Equals(candidate, scheme, StringComparison.OrdinalIgnoreCase));
            if (matchedInstance is null)
            {
                continue;
            }

            var family = MemorySourceChoices.FirstOrDefault(choice =>
                string.Equals(choice.FamilyKey, familyKey, StringComparison.OrdinalIgnoreCase));
            if (family is null)
            {
                continue;
            }

            top = family;
            instance = matchedInstance;
            return true;
        }

        top = null;
        instance = null;
        return false;
    }

    // Whether the memory-source picker is shown for this row — only for a Memory row, and only once something
    // registered a source (AC-166's `HasMemorySources`, answered per row instead of once for the dialog).
    public bool ShowsMemorySourcePicker => Role == ProjectResourceRole.Memory && MemorySourceChoices.Count > 0;

    // Whether to show a confirmation under this Memory row (AC-503) — the counterpart to `IsBroken` for a
    // plugin-registered source rather than a filesystem path (AC-499).
    public bool IsConfirmedReachable =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.Confirmed;

    // The AC-503 "not found" state — see `IsConfirmedReachable`'s own remarks on the shared gating.
    public bool IsNotFoundReachable =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.NotFound;

    // The AC-503 "not signed in" state — see `IsConfirmedReachable`'s own remarks on the shared gating.
    public bool IsNotSignedIn =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.NotSignedIn;

    // The AC-499 "the check itself failed" state — see `IsConfirmedReachable`'s own remarks on the shared gating.
    public bool IsCheckFailed =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.CheckFailed;

    // See `ProjectResource.SendsContent`'s own doc comment for why the other two roles never offer it: a memory row is
    // read and written back to all session long and far too large to inline, and a reference row exists to be looked
    // up, not read up front (AC-486).
    public bool ShowsSendsContentOption => Role == ProjectResourceRole.Instructions;

    // Whether `Reference` holds a folder path rather than a source's bare value — gates "Choose…" for a Memory row the
    // same way the single Memory row used to (AC-499).
    public bool IsMemoryFolderMode =>
        SelectedMemorySourceChoice is not { } choice
        || (choice.Scheme is not { Length: > 0 } && choice.FamilyKey is not { Length: > 0 });

    // The choice this row actually means right now, whichever axis carries it (AC-499): the top choice itself for
    // "Folder" or an ungrouped source, `SelectedFamilyInstance` when the top choice is a family (null when that family
    // has no instance picked).
    public MemorySourceChoice? SelectedMemorySourceLeaf =>
        SelectedMemorySourceChoice?.FamilyKey is not null ? SelectedFamilyInstance : SelectedMemorySourceChoice;

    // AC-502 narrows that: it stays insensitive only for a source that cannot enumerate its own locations
    // (`MemorySourceChoice.ListLocationsAsync` null); one that can opens a picker of names instead of a folder browser
    // (AC-499).
    public bool CanBrowse =>
        Role != ProjectResourceRole.Memory
        || IsMemoryFolderMode
        || SelectedMemorySourceLeaf?.ListLocationsAsync is not null;

    // Whether the server row — the instance dropdown (or its empty-state hint) plus "Servers…" — is shown under
    // this row's own source picker (AC-499). Only when the top choice is a family: an ungrouped source or Folder
    // has no second axis to pick from at all.
    public bool ShowsMemorySourceServerRow => Role == ProjectResourceRole.Memory && SelectedMemorySourceChoice?.FamilyKey is not null;

    // The picked family's own instances (AC-499) — empty when the top choice is not a family, or is a family with nothing registered under it yet.
    public IReadOnlyList<MemorySourceChoice> FamilyInstanceChoices =>
        SelectedMemorySourceChoice?.FamilyKey is { } familyKey && _familyInstanceChoicesByKey.TryGetValue(familyKey, out var instances)
            ? instances
            : [];

    // Whether the picked family actually has an instance to offer — gates showing the instance dropdown itself rather than `MemorySourceInstanceEmptyHint` in its place.
    public bool HasFamilyInstances => FamilyInstanceChoices.Count > 0;

    // What the server row shows in place of the instance dropdown when the picked family has none (AC-499) — the family's own `ProjectMemorySourceFamily.EmptyHint`, or a generic fallback for a family that never set one.
    public string? MemorySourceInstanceEmptyHint => SelectedMemorySourceChoice?.EmptyHint ?? "No server configured yet.";

    // Whether the server row's "Servers…" button does anything at all (AC-499) — never a dead button, the same rule `CanBrowse` already follows for "Choose…".
    public bool CanConfigureMemorySource => SelectedMemorySourceChoice?.ConfigureAsync is not null;

    // The line under a Memory row's picker (AC-166): stops calling the location a folder once a source other than
    // Folder is picked — the same reasoning the single Memory row's own hint carried before this row replaced it
    // (AC-499).
    public string MemoryHint =>
        !IsMemoryFolderMode
            ? "Where this project's memory lives — the name it goes by in the source above, not a path. Sessions are told about it, so they can look things up instead of being told again."
            : "Where this project's memory lives — a folder, kept apart from the source folder. Sessions are told about it, so they can look things up instead of being told again.";

    // What the reference box hints at: a folder or identifier for a Memory row (mirroring the old single row), a
    // plain file/folder hint for the other two roles. AC-499: names the picked instance where there is one, falling
    // back to the family's own label when a family is picked but no instance has been chosen yet.
    public string ReferencePlaceholder =>
        Role switch
        {
            ProjectResourceRole.Memory when !IsMemoryFolderMode =>
                $"An identifier {SelectedMemorySourceLeaf?.Label ?? SelectedMemorySourceChoice?.Label} understands",
            ProjectResourceRole.Memory => "No memory location",
            _ => "A file or folder path",
        };

    // Whether this row has neither a reference nor a label — the same "untouched" shape `ProjectInfoFieldViewModel` drops on save.
    public bool IsBlank => string.IsNullOrWhiteSpace(Reference) && string.IsNullOrWhiteSpace(Label);

    // Deliberately full sentences rather than a terse ◆/● badge: AC-604 already put a two-glyph ownership badge next to
    // a project field's own `Origin`, and a second badge language reading the same way on the same screen would tell
    // the operator nothing about which question either one answers (AC-605, AC-482).
    public string? ScopeLabel => Scope switch
    {
        ProjectResourceScope.Repo => "Travels with the repo.",
        ProjectResourceScope.Home => "Anchored to a home folder — travels to everyone this project is shared with, resolved against whoever opens it.",
        ProjectResourceScope.Instance => "Resolved through its plugin's own connection — the same reference works for anyone with access to it.",
        ProjectResourceScope.Machine => "This is an absolute path specific to this machine — it will not travel if the project definition is shared.",
        _ => null,
    };

    // AC-612: hidden the same way once `IsSecretPath` is true (AC-605).
    public bool ShowsScopeLabel => ScopeLabel is not null && !HasRepoRelativeFix && !IsSecretPath;

    // AC-612: also false whenever `IsSecretPath` is true, even if `RepoRelativeFix` itself is not null (AC-605).
    public bool HasRepoRelativeFix => RepoRelativeFix is not null && !IsSecretPath;

    // A pure, synchronous computation over `Reference` alone — no I/O, no debounce, unlike
    // `Scope`/`IsBroken`/`RepoRelativeFix`, which `ProjectDialogViewModel` computes off the UI thread because they cost
    // real disk access (AC-612).
    public bool IsSecretPath => ProjectResourceSecretPathHeuristic.IsLikelySecretPath(Reference);

    // The sentence shown for a row `IsSecretPath` recognises (AC-612) — null (hidden) otherwise.
    public string? SecretPathWarning => !IsSecretPath
        ? null
        : Role == ProjectResourceRole.Instructions
            ? "This path looks like it holds credentials — its content will never be sent to a session, and this row will not be included if the project definition is shared."
            : "This path looks like it holds credentials — this row will not be included if the project definition is shared.";

    // Applies `RepoRelativeFix` in place (AC-605 criterion 5).
    [RelayCommand]
    private void ApplyRepoRelativeFix()
    {
        if (RepoRelativeFix is { } fix)
        {
            Reference = fix;
        }
    }

    // A blank value under a picked scheme saves as no reference at all (matching `_ToMemoryRef`'s own rule) rather than
    // a bare `"{scheme}:"` that names a source and nothing in it (AC-166, AC-499).
    public ProjectResource ToDomain()
    {
        var typed = Reference.Trim();
        var reference = ShowsMemorySourcePicker && !IsMemoryFolderMode && SelectedMemorySourceLeaf is { Scheme.Length: > 0 } leaf
            ? typed.Length > 0 ? $"{leaf.Scheme}:{typed}" : string.Empty
            : typed;

        return new ProjectResource(reference, Role)
        {
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            ReachesSessions = ReachesSessions,
            SendsContent = SendsContent,
        };
    }
}

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// One editable row of a project's resources (AC-485): something a session reads, follows or looks up beside its
// own source folder — see `ProjectResource` and `ProjectResourceRole`. Mirrors the
// `ProjectInfoFieldViewModel` idiom: a row the operator adds and can leave alone, dropped on save
// rather than held against them (`IsBlank`).
//
// The standalone "Memory" row the project editor used to carry on its own (AC-165/166) is now simply a row here
// with `Role` set to `ProjectResourceRole.Memory` — `MemorySourceChoices`,
// `SelectedMemorySourceChoice` and `MemoryHint` are that row's picker, carried over
// per-row instead of once for the whole dialog.
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

    // Whether a session started on this project is told this row (AC-483/484). Defaults to true — the opposite
    // default from `ProjectInfoFieldViewModel.IsSharedWithSessions`, deliberately: an information row
    // arrives as reference material for the operator first, while a memory or instruction row exists specifically
    // to be told to the session that reads or obeys it. Both defaults are named in the dialog itself, so the two
    // checkboxes defaulting oppositely reads as a choice rather than a surprise.
    [ObservableProperty]
    private bool _reachesSessions;

    // Whether this row's contents travel with a starting session rather than only its location (AC-486) — see
    // `ProjectResource.SendsContent`'s own doc comment for why this defaults off and is offered for
    // `ProjectResourceRole.Instructions` alone. Reset to false whenever `Role` changes away
    // from Instructions (see `OnRoleChanged`) — the same "a role switch cannot leave a control quietly
    // meaning something on a row it no longer applies to" rule `ShowsMemorySourcePicker`'s own reset
    // already follows, just the other direction: nothing here needs folding into `Reference` first,
    // since this flag never changed what the box shows, only what a session is given alongside it.
    [ObservableProperty]
    private bool _sendsContent;

    // The picker's current choice for this row's memory source (AC-166), or null when this row is not a Memory row,
    // nothing was ever picked, or "Folder" is what it means. `MemorySourceChoices` is the same list for
    // every row in the dialog — the registry does not change per row, only which entry each row picked.
    //
    // AC-499: this is the top-level "kind of place" axis alone. A choice whose `MemorySourceChoice.FamilyKey`
    // is set names a family, not an instance — `SelectedFamilyInstance` is the second axis that names
    // which one, and `SelectedMemorySourceLeaf` is the one property that answers "which scheme, if any,
    // does this row actually mean right now" regardless of which axis carries it.
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

    // Which instance of `SelectedMemorySourceChoice`'s family this row picked (AC-499) — "Depot
    // (krahwinkel-it)" out of however many connections that family currently has. Null when the top choice is not a
    // family (Folder, or an ungrouped source — see `SelectedMemorySourceChoice`'s own remarks), or when
    // it is a family with no registered instance yet. Reset to the family's first instance (or null, if it has
    // none) the instant `SelectedMemorySourceChoice` changes — see `OnSelectedMemorySourceChoiceChanged`.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    private MemorySourceChoice? _selectedFamilyInstance;

    // Whether `ProjectDialogViewModel`'s "Servers…" command is mid-flight for this row (AC-499) —
    // mirrors `ProjectPluginFieldViewModel.IsLoadingOptions`/`CloneFromGitUrlDialogViewModel.IsCloning`'s
    // own busy-guard shape. Disables the button rather than hiding it (`CanConfigureMemorySource` stays
    // about whether a configure action exists at all, never about whether one is currently running) so a slow or
    // hung `ProjectMemorySourceFamily.ConfigureAsync` reads as "working", not as the button vanishing
    // mid-click. Also guards against a double-invoke from an impatient second click while the first is still out.
    [ObservableProperty]
    private bool _isConfiguringMemorySource;

    // Set when the last "Servers…" call failed (AC-499), shown under the server row the same way
    // `ProjectPluginFieldViewModel.LoadError` is shown under a plugin field's own options list. Cleared
    // at the start of every call so a stale failure never survives a successful retry. A plugin's
    // `ProjectMemorySourceFamily.ConfigureAsync` throwing must cost this row a message, not the whole
    // dialog an unobserved faulted `Task` would otherwise leave behind.
    [ObservableProperty]
    private string? _memorySourceConfigureError;

    // Whether this reference could not be found (AC-485), set from outside — by
    // `Cockpit.Infrastructure.Projects.ProjectResourceProbe`, run by the dialog after any row changes — rather
    // than computed here: the probe's own rules (absolute paths only, no UNC, a shared time budget) belong with the
    // probe, not duplicated on this row. A row this is never set on (a brand-new row, a scene that only cares about
    // the rest of the layout) simply reads as "not known to be broken", the same as any row the probe declined to
    // judge at all.
    [ObservableProperty]
    private bool _isBroken;

    // How far this reference travels (AC-605 criteria 6, 7) — set from outside, by
    // `ProjectDialogViewModel`, from `Cockpit.Core.Projects.ProjectResourcePathPortability.ClassifyScope`.
    // Renamed from the old `IsMachineBound` bool (AC-605 criterion 6): a property that only ever answered
    // "machine-bound or not" could not tell an anchor-relative reference (travels to any machine the operator
    // opens the project on) apart from a repo-relative one (travels with the repo) — both simply read as "not
    // machine-bound". Null for a row this was never judged for (a brand-new row, a blank reference).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeLabel))]
    [NotifyPropertyChangedFor(nameof(ShowsScopeLabel))]
    private ProjectResourceScope? _scope;

    // The repo-relative form this row's `Reference` should have been saved as, when it is a fully
    // qualified path that already lives inside the project's own folder but was never actually converted (AC-605
    // criterion 5) — hand-typed, or written by hand into `cockpit.json`. Set from outside by
    // `ProjectDialogViewModel` from `Cockpit.Core.Projects.ProjectResourcePathPortability.SuggestRepoRelativeFix`.
    // Null when there is nothing to fix, which gates `HasRepoRelativeFix` and the editor's own
    // remediation action.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepoRelativeFix))]
    [NotifyPropertyChangedFor(nameof(ShowsScopeLabel))]
    private string? _repoRelativeFix;

    // What a Memory row's own registered source found about the typed value (AC-503), or null when nothing is
    // known yet — a brand-new row, a row whose source has no `MemorySourceChoice.CheckReachability`
    // (the "no check available" default every source had before AC-503), a row that is not a Memory row, or one
    // reset by `_ResetReachability` the instant `Reference`, `Role` or
    // `SelectedMemorySourceChoice` changes — the same "a value that is about to be judged again must
    // not keep showing the previous judgement" rule `IsBroken`'s own dialog-driven refresh follows,
    // just applied immediately here rather than only once the debounced check answers, since this one is a network
    // call and can take long enough that a stale confirmation sitting under a row the operator just changed would
    // read as still true. Set from outside by `ProjectDialogViewModel` once its own check completes.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    private ProjectMemorySourceReachability? _reachability;

    // The text to show under a `ProjectMemorySourceReachability.Confirmed` or (AC-499)
    // `ProjectMemorySourceReachability.CheckFailed` row — the plugin's own
    // `ProjectMemorySourceReachabilityResult.Detail`, or null to fall back to each state's own fixed
    // sentence. Ignored for `ProjectMemorySourceReachability.NotSignedIn`/`ProjectMemorySourceReachability.NotFound`,
    // the same restraint that result type's own doc comment describes.
    [ObservableProperty]
    private string? _reachabilityDetail;

    // Whether this is the last row in `ProjectDialogViewModel.ResourceRows` — set from outside, by the
    // dialog, whenever a row is added or removed (AC-485 review, FIX 8). Gates the row's own bottom divider in the
    // view: the last row has no next row to separate itself from, so it must draw no line at all. Kept as a plain
    // property set explicitly rather than answered by a binding that reaches back into the dialog's own
    // `ResourceRows` list — that binding was tried first and measured not to re-run reliably when a row was
    // added or removed (`ResourceRows` itself is the same collection reference for the dialog's whole
    // lifetime, so nothing about it changing triggers a re-bind on its own), where a plain property the dialog sets
    // explicitly cannot go stale the same way.
    [ObservableProperty]
    private bool _isLastRow;

    // The choices offered for this row's memory source — the same list, shared, for every row (AC-166).
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; }

    // Every family's own instances, shared for every row the same way `MemorySourceChoices` is (AC-499)
    // — keyed the same case-insensitive way `ProjectMemorySourceFamily.Key` is matched. Not readonly
    // (AC-523): `UpdateFamilyInstanceChoices` swaps this for a freshly rebuilt dictionary once the
    // "Servers…" flow's own settings screen may have added or removed an instance, rather than this row staying
    // pinned to whatever `ProjectDialogViewModel.CreateAsync` handed it when the dialog first opened.
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
        // AC-612: a row loaded already ticked for a secret-shaped reference (a hand-edited cockpit.json, or one
        // saved before this ticket existed) must never arrive checked — see OnReferenceChanged's own remarks for
        // the live-edit half of this, and ProjectResource.SendsContent's own getter for the belt this is the
        // braces to (the domain model enforces it too, but a checkbox that reads "on" while doing nothing is
        // exactly the silent-field failure this ticket exists to rule out).
        _sendsContent = sendsContent && !ProjectResourceSecretPathHeuristic.IsLikelySecretPath(reference);
    }

    // Swaps in a freshly rebuilt family-instances dictionary (AC-523) — called by
    // `ProjectDialogViewModel.ConfigureMemorySourceAsync` once its own "Servers…" call returns, so a
    // connection added or removed in the settings screen that call opened shows up here without the operator having
    // to close and reopen the whole project dialog.
    //
    // `SelectedFamilyInstance` is re-matched by `MemorySourceChoice.Scheme` against the new
    // dictionary rather than left as-is: the old instance object is never in the new dictionary (each rebuild
    // constructs fresh `MemorySourceChoice` records), so a reference-equality read would read every
    // previously-selected instance as gone. A scheme still offered keeps its selection (AC-523 criterion 2); a
    // scheme no longer offered — the operator removed that connection while the settings screen was open — falls
    // back to no selection rather than silently keep pointing at something gone (AC-523 criterion 3).
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
    // `ShowsMemorySourcePicker` hides the source picker the instant `Role` stops being
    // `ProjectResourceRole.Memory` — but before this method existed, the scheme that picker had folded
    // away stayed folded: switching a row from Memory (source "Depot project", box showing "cockpit") to Reference
    // left the box still showing the bare value while `ToDomain` kept saving `depot:cockpit`
    // underneath — a reference to nothing, with nothing on screen telling the operator it had changed. Switching
    // back to Memory happened to repair it, which is what made the bug easy to miss: only a role change that
    // *stuck* actually lost anything.
    //
    // Both directions are handled, mirroring `ProjectDialogViewModel.CreateAsync`'s own load-time fold/unfold
    // of a saved reference so a row behaves the same whether the shape it is given came from disk or from the
    // operator switching roles just now:
    // - <description>
    // Away from Memory, with a source other than Folder picked: the scheme is folded into `Reference`
    // right now — the box changes in front of the operator instead of the change happening silently at save — and
    // the picker's selection is dropped back to Folder (or cleared, when there is no picker), since the picker is
    // about to disappear and nothing should be left pointing at a source the box no longer names.
    // </description>
    // - <description>
    // Onto Memory, with the box holding a `&lt;scheme&gt;:&lt;value&gt;` reference to a source this dialog
    // actually offers: that source is selected and the box is set to the bare value, exactly as a freshly loaded
    // row would show it. Anything else — a plain path, an unrecognised scheme — leaves Folder selected and the box
    // untouched, the same "leave it alone rather than guess" rule `CreateAsync` already applies.
    // </description>
    partial void OnRoleChanged(ProjectResourceRole oldValue, ProjectResourceRole newValue)
    {
        // AC-503: a role switch changes what Reference means (see this method's own remarks below on the Memory
        // fold/unfold), and a Reachability answer belongs to a specific typed value against a specific source —
        // neither of which this switch can be trusted to leave alone. Unconditional, at the top, rather than folded
        // into one of the branches below: every branch either reassigns Reference/SelectedMemorySourceChoice itself
        // (which resets this the same way, see OnReferenceChanged/OnSelectedMemorySourceChoiceChanged below) or
        // leaves both alone entirely (switching to Memory with no picker registered at all) — the second case would
        // otherwise carry a stale answer forward with nothing else here to catch it.
        _ResetReachability();

        // AC-486: leaving Instructions must not leave "Send along" quietly ticked on a row where it now means
        // nothing — the checkbox is about to disappear (see ShowsSendsContentOption below), and nothing reads this
        // flag for any other role. The other direction needs nothing here: ProjectResource.SendsContent reports
        // false for any role but Instructions, so a stored tick on a row that was never an instruction cannot
        // arrive pre-ticked by switching into the role. (It could, until the review round measured it.)
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

    // AC-503: a Reachability answer belongs to a specific typed value — an edit invalidates it the instant it
    // happens, before any debounced re-check can even start.
    //
    // AC-612: also the live half of the secret-path guard the constructor applies at load — `IsSecretPath`
    // is a pure, synchronous computation (no debounce, no async diagnostics pass to wait for; see its own remarks
    // on why), so the instant a typed reference starts looking like credential material, "Send along" is switched
    // off right here, in front of the operator, rather than silently doing nothing the next time this row is saved.
    partial void OnReferenceChanged(string value)
    {
        _ResetReachability();

        if (IsSecretPath)
        {
            SendsContent = false;
        }
    }

    // AC-612: the belt to `OnReferenceChanged`'s braces — that method catches the tick the instant the
    // reference starts looking secret, but says nothing about the tick being set directly while the reference
    // already does (nothing in this class currently does that outside a test, but the disabled checkbox binding
    // alone is a UI-only guard: it stops a click, not an assignment). Without this, the checkbox could end up
    // showing checked-but-disabled — visibly wrong, exactly the silent-field failure this ticket exists to rule
    // out — for however long it took the next reference edit or a save round-trip through
    // `ProjectResource.SendsContent`'s own getter to correct it.
    partial void OnSendsContentChanged(bool value)
    {
        if (value && IsSecretPath)
        {
            SendsContent = false;
        }
    }

    // AC-503: a Reachability answer belongs to a specific source — picking a different one (or Folder) invalidates
    // whatever the previous source found. AC-499: also resets the instance axis to the newly picked family's own
    // first instance (or null, if it has none, or the new top choice is not a family at all) — a stale instance
    // pointing at the previous family's own connections must not survive the top choice changing out from under it.
    // A caller that then wants a specific instance (loading a stored reference, a role switch back onto Memory)
    // overwrites this immediately after, the same "default then overwrite" order `CreateAsync`'s own Folder
    // preselection already uses.
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

    // Finds which top-level choice and, if it is a family, which of its instances a saved `scheme`
    // names (AC-499) — one layer deeper than the pre-AC-499 search that only ever looked at
    // `MemorySourceChoices` itself. Shared by `ProjectDialogViewModel.CreateAsync` (loading a
    // stored reference) and `OnRoleChanged` (a role switch back onto Memory with a scheme already
    // typed) rather than duplicated in both — the same search either way.
    //
    // `top`: The top-level choice to select, or null when nothing matches.
    // `instance`: The family instance to select alongside `top`, or null when `top` is an ungrouped source (or nothing matched at all).
    // True when `scheme` names a source this row can actually offer.
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

    // Whether to show a confirmation under this Memory row (AC-503) — the counterpart to `IsBroken` for
    // a plugin-registered source rather than a filesystem path. Gated on `Role` and a non-blank typed
    // value the same way `IsNotFoundReachable`/`IsNotSignedIn`/`IsCheckFailed`
    // are, so a blank field never shows any of the four (AC-503 acceptance criterion 6, AC-499) whatever
    // `Reachability` last held from before the field was cleared — clearing the field itself already
    // resets it (see `OnReferenceChanged`), but this gate is the belt to that braces.
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

    // Whether "Send along" is offered for this row at all (AC-486) — `ProjectResourceRole.Instructions`
    // alone, the same per-role gating idiom `ShowsMemorySourcePicker` already uses. See
    // `ProjectResource.SendsContent`'s own doc comment for why the other two roles never offer it: a
    // memory row is read and written back to all session long and far too large to inline, and a reference row
    // exists to be looked up, not read up front.
    public bool ShowsSendsContentOption => Role == ProjectResourceRole.Instructions;

    // Whether `Reference` holds a folder path rather than a source's bare value — gates "Choose…" for a
    // Memory row the same way the single Memory row used to. AC-499: a family is never folder mode, even with no
    // instance picked yet (nothing has ever been chosen to browse a folder in place of) — `SelectedMemorySourceLeaf`
    // being null in that state is "nothing to fold a scheme from", not "this means a folder".
    public bool IsMemoryFolderMode =>
        SelectedMemorySourceChoice is not { } choice
        || (choice.Scheme is not { Length: > 0 } && choice.FamilyKey is not { Length: > 0 });

    // The choice this row actually means right now, whichever axis carries it (AC-499): the top choice itself for
    // "Folder" or an ungrouped source, `SelectedFamilyInstance` when the top choice is a family (null
    // when that family has no instance picked). What `ToDomain` folds a scheme from, what
    // `ProjectDialogViewModel`'s reachability check calls, and what `ProjectDialog`'s own "Choose…"
    // handler opens a location picker with.
    public MemorySourceChoice? SelectedMemorySourceLeaf =>
        SelectedMemorySourceChoice?.FamilyKey is not null ? SelectedFamilyInstance : SelectedMemorySourceChoice;

    // Whether "Choose…" does anything at all. A Memory row with a source other than Folder picked used to always
    // take a typed identifier, not a path — the same reason the single Memory row's own button used to go
    // insensitive. AC-502 narrows that: it stays insensitive only for a source that cannot enumerate its own
    // locations (`MemorySourceChoice.ListLocationsAsync` null); one that can opens a picker of names
    // instead of a folder browser. Every other role always browses (for a file — see `ReferencePlaceholder`).
    // AC-499: a family with no instance picked has no `SelectedMemorySourceLeaf` to ask, so this reads
    // false the same as a source with no picker — nothing to browse until an instance exists to browse it with.
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
    // Folder is picked — the same reasoning the single Memory row's own hint carried before this row replaced it.
    // AC-499: a family reads as "not a folder" the instant it is picked, whether or not an instance has been chosen
    // under it yet — the operator already committed to typing an identifier, not browsing a folder.
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

    // The sentence shown under this row for its `Scope` (AC-605 criterion 7) — null (hidden) for a row
    // with no scope known yet. Deliberately full sentences rather than a terse ◆/● badge: AC-604 already put a
    // two-glyph ownership badge next to a project field's own `Origin`, and a second badge language reading
    // the same way on the same screen would tell the operator nothing about which question either one answers. The
    // `ProjectResourceScope.Machine` wording is unchanged from the pre-AC-605 hint on purpose — the one
    // case this scene's own render already showed (see `Screenshotter._ProjectEditorWithResources`).
    //
    // AC-605 review round (Raymond): `ProjectResourceScope.Home`'s sentence used to lead with "your
    // home folder — travels to any machine *you* open this project on", the AC-482 framing this ticket
    // reverses. This is the one place an operator can see whether a row leaves the machine at all — reading that
    // sentence, a `~/...` row looked like it stayed put, when criterion 3 already made it travel in
    // `.cockpit/project.json` to everyone the project is shared with. Rewritten so "shared" is the sentence's
    // own point, not an implication left for the operator to work out.
    public string? ScopeLabel => Scope switch
    {
        ProjectResourceScope.Repo => "Travels with the repo.",
        ProjectResourceScope.Home => "Anchored to a home folder — travels to everyone this project is shared with, resolved against whoever opens it.",
        ProjectResourceScope.Instance => "Resolved through its plugin's own connection — the same reference works for anyone with access to it.",
        ProjectResourceScope.Machine => "This is an absolute path specific to this machine — it will not travel if the project definition is shared.",
        _ => null,
    };

    // Whether `ScopeLabel` is actually shown (AC-605 review round) — hidden once
    // `HasRepoRelativeFix` is true, even though `Scope` is still `ProjectResourceScope.Machine`
    // in that state: the fix banner's own sentence already says this path will not travel and, unlike the plain
    // scope sentence, also offers the remedy — showing both repeated the same closing clause ("it will not travel
    // if the project definition is shared") twice in a row for no benefit. Found rendering the AC-605 scope-scene:
    // with both shown, a row with a fix available cost two lines' worth of near-identical text where one said
    // everything the other did and more.
    //
    // AC-612: hidden the same way once `IsSecretPath` is true — `SecretPathWarning` is the
    // more specific, more urgent thing to say about this row, and a plain "travels to everyone" or "stays on this
    // machine" sentence sitting next to it would read as a second, competing answer to "does this leave the
    // machine" rather than as a detail. One row, one primary explanation (the same reasoning that already governs
    // `HasRepoRelativeFix` above).
    public bool ShowsScopeLabel => ScopeLabel is not null && !HasRepoRelativeFix && !IsSecretPath;

    // Whether `RepoRelativeFix` has something to offer (AC-605 criterion 5) — gates the editor's own
    // "make repo-relative" action.
    //
    // AC-612: also false whenever `IsSecretPath` is true, even if `RepoRelativeFix` itself
    // is not null — Raymond's decision explicitly rules out building any escape from the secret-path gate, and an
    // unguarded button here would quietly be one: repo-relative is a shape `ProjectResourceSecretPathHeuristic`
    // never evaluates at all (see its own class remarks on scope), so clicking "Make repo-relative" on a row like
    // `SourceDirectory/.ssh/id_rsa` would rewrite it to `.ssh/id_rsa` and walk it straight out of every
    // check this ticket adds — the exact "toch delen"-escape the ticket says not to build, just reached through a
    // button that already existed for an unrelated reason.
    public bool HasRepoRelativeFix => RepoRelativeFix is not null && !IsSecretPath;

    // Whether `Reference` resolves to a location `ProjectResourceSecretPathHeuristic`
    // recognises as likely credential material (AC-612). A pure, synchronous computation over `Reference`
    // alone — no I/O, no debounce, unlike `Scope`/`IsBroken`/`RepoRelativeFix`,
    // which `ProjectDialogViewModel` computes off the UI thread because they cost real disk access.
    // This costs nothing to compute, so it is computed right here instead: the alternative (routing it through that
    // same async pass) would mean "Send along" stays checked, and no warning shows, for up to that pass's own
    // 400 ms quiet period after every keystroke — a window Raymond's "never send it" decision has no room for.
    public bool IsSecretPath => ProjectResourceSecretPathHeuristic.IsLikelySecretPath(Reference);

    // The sentence shown for a row `IsSecretPath` recognises (AC-612) — null (hidden) otherwise. Names
    // only the path's own shape, never a character of what the file actually holds (Iron Law #8: this heuristic
    // never reads the file at all, so it has no content to name even by accident). Sharper when
    // `ShowsSendsContentOption` is offered at all (an Instructions row): that is the one role where a
    // tick away from this row's content going out, so it is the one role whose sentence says so.
    public string? SecretPathWarning => !IsSecretPath
        ? null
        : Role == ProjectResourceRole.Instructions
            ? "This path looks like it holds credentials — its content will never be sent to a session, and this row will not be included if the project definition is shared."
            : "This path looks like it holds credentials — this row will not be included if the project definition is shared.";

    // Applies `RepoRelativeFix` in place (AC-605 criterion 5) — the one path that ever rewrites
    // `Reference` for an absolute-but-already-inside-the-folder row: an explicit action the operator
    // took, never automatic (see `Cockpit.Core.Projects.ProjectResourcePathPortability`'s own remarks
    // on why only a picked path is rewritten without being asked).
    [RelayCommand]
    private void ApplyRepoRelativeFix()
    {
        if (RepoRelativeFix is { } fix)
        {
            Reference = fix;
        }
    }

    // This row as the domain model that is actually saved: the scheme folded into the reference when a Memory row
    // has a source other than Folder picked (AC-166) — the same fold `ProjectDialogViewModel._ToMemoryRef`
    // used to do once for the whole dialog, now done per row. A blank value under a picked scheme saves as no
    // reference at all (matching `_ToMemoryRef`'s own rule) rather than a bare `"{scheme}:"` that names a
    // source and nothing in it. AC-499: the scheme comes from `SelectedMemorySourceLeaf` — a family
    // with no instance picked has none to fold, so the typed text saves unprefixed, the same as Folder, rather than
    // inventing a scheme that names nothing.
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

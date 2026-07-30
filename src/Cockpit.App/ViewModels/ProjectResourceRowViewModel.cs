using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One editable row of a project's resources (AC-485): something a session reads, follows or looks up beside its
/// own source folder — see <see cref="ProjectResource"/> and <see cref="ProjectResourceRole"/>. Mirrors the
/// <see cref="ProjectInfoFieldViewModel"/> idiom: a row the operator adds and can leave alone, dropped on save
/// rather than held against them (<see cref="IsBlank"/>).
/// <para>
/// The standalone "Memory" row the project editor used to carry on its own (AC-165/166) is now simply a row here
/// with <see cref="Role"/> set to <see cref="ProjectResourceRole.Memory"/> — <see cref="MemorySourceChoices"/>,
/// <see cref="SelectedMemorySourceChoice"/> and <see cref="MemoryHint"/> are that row's picker, carried over
/// per-row instead of once for the whole dialog.
/// </para>
/// </summary>
public partial class ProjectResourceRowViewModel : ViewModelBase
{
    /// <summary>Every role the picker offers, in the order <see cref="ProjectResourceRole"/> declares them.</summary>
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
    private ProjectResourceRole _role;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    private string _reference;

    [ObservableProperty]
    private string _label;

    /// <summary>
    /// Whether a session started on this project is told this row (AC-483/484). Defaults to true — the opposite
    /// default from <see cref="ProjectInfoFieldViewModel.IsSharedWithSessions"/>, deliberately: an information row
    /// arrives as reference material for the operator first, while a memory or instruction row exists specifically
    /// to be told to the session that reads or obeys it. Both defaults are named in the dialog itself, so the two
    /// checkboxes defaulting oppositely reads as a choice rather than a surprise.
    /// </summary>
    [ObservableProperty]
    private bool _reachesSessions;

    /// <summary>
    /// Whether this row's contents travel with a starting session rather than only its location (AC-486) — see
    /// <see cref="ProjectResource.SendsContent"/>'s own doc comment for why this defaults off and is offered for
    /// <see cref="ProjectResourceRole.Instructions"/> alone. Reset to false whenever <see cref="Role"/> changes away
    /// from Instructions (see <see cref="OnRoleChanged"/>) — the same "a role switch cannot leave a control quietly
    /// meaning something on a row it no longer applies to" rule <see cref="ShowsMemorySourcePicker"/>'s own reset
    /// already follows, just the other direction: nothing here needs folding into <see cref="Reference"/> first,
    /// since this flag never changed what the box shows, only what a session is given alongside it.
    /// </summary>
    [ObservableProperty]
    private bool _sendsContent;

    /// <summary>
    /// The picker's current choice for this row's memory source (AC-166), or null when this row is not a Memory row,
    /// nothing was ever picked, or "Folder" is what it means. <see cref="MemorySourceChoices"/> is the same list for
    /// every row in the dialog — the registry does not change per row, only which entry each row picked.
    /// <para>
    /// AC-499: this is the top-level "kind of place" axis alone. A choice whose <see cref="MemorySourceChoice.FamilyKey"/>
    /// is set names a family, not an instance — <see cref="SelectedFamilyInstance"/> is the second axis that names
    /// which one, and <see cref="SelectedMemorySourceLeaf"/> is the one property that answers "which scheme, if any,
    /// does this row actually mean right now" regardless of which axis carries it.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Which instance of <see cref="SelectedMemorySourceChoice"/>'s family this row picked (AC-499) — "Depot
    /// (krahwinkel-it)" out of however many connections that family currently has. Null when the top choice is not a
    /// family (Folder, or an ungrouped source — see <see cref="SelectedMemorySourceChoice"/>'s own remarks), or when
    /// it is a family with no registered instance yet. Reset to the family's first instance (or null, if it has
    /// none) the instant <see cref="SelectedMemorySourceChoice"/> changes — see <see cref="OnSelectedMemorySourceChoiceChanged"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    private MemorySourceChoice? _selectedFamilyInstance;

    /// <summary>
    /// Whether <see cref="ProjectDialogViewModel"/>'s "Servers…" command is mid-flight for this row (AC-499) —
    /// mirrors <see cref="ProjectPluginFieldViewModel.IsLoadingOptions"/>/<c>CloneFromGitUrlDialogViewModel.IsCloning</c>'s
    /// own busy-guard shape. Disables the button rather than hiding it (<see cref="CanConfigureMemorySource"/> stays
    /// about whether a configure action exists at all, never about whether one is currently running) so a slow or
    /// hung <see cref="ProjectMemorySourceFamily.ConfigureAsync"/> reads as "working", not as the button vanishing
    /// mid-click. Also guards against a double-invoke from an impatient second click while the first is still out.
    /// </summary>
    [ObservableProperty]
    private bool _isConfiguringMemorySource;

    /// <summary>
    /// Set when the last "Servers…" call failed (AC-499), shown under the server row the same way
    /// <see cref="ProjectPluginFieldViewModel.LoadError"/> is shown under a plugin field's own options list. Cleared
    /// at the start of every call so a stale failure never survives a successful retry. A plugin's
    /// <see cref="ProjectMemorySourceFamily.ConfigureAsync"/> throwing must cost this row a message, not the whole
    /// dialog an unobserved faulted <see cref="Task"/> would otherwise leave behind.
    /// </summary>
    [ObservableProperty]
    private string? _memorySourceConfigureError;

    /// <summary>
    /// Whether this reference could not be found (AC-485), set from outside — by
    /// <see cref="Cockpit.Infrastructure.Projects.ProjectResourceProbe"/>, run by the dialog after any row changes — rather
    /// than computed here: the probe's own rules (absolute paths only, no UNC, a shared time budget) belong with the
    /// probe, not duplicated on this row. A row this is never set on (a brand-new row, a scene that only cares about
    /// the rest of the layout) simply reads as "not known to be broken", the same as any row the probe declined to
    /// judge at all.
    /// </summary>
    [ObservableProperty]
    private bool _isBroken;

    /// <summary>
    /// Whether this reference names an absolute path outside the project's own folder (AC-485) — a reference that
    /// means nothing on a machine that has this project somewhere else, or does not have it at all. Set from
    /// outside the same way <see cref="IsBroken"/> is (see <see cref="Cockpit.Core.Projects.ProjectResourcePathPortability"/>),
    /// since judging it needs <c>SourceDirectory</c>, which this row does not itself know.
    /// </summary>
    [ObservableProperty]
    private bool _isMachineBound;

    /// <summary>
    /// What a Memory row's own registered source found about the typed value (AC-503), or null when nothing is
    /// known yet — a brand-new row, a row whose source has no <see cref="MemorySourceChoice.CheckReachability"/>
    /// (the "no check available" default every source had before AC-503), a row that is not a Memory row, or one
    /// reset by <see cref="_ResetReachability"/> the instant <see cref="Reference"/>, <see cref="Role"/> or
    /// <see cref="SelectedMemorySourceChoice"/> changes — the same "a value that is about to be judged again must
    /// not keep showing the previous judgement" rule <see cref="IsBroken"/>'s own dialog-driven refresh follows,
    /// just applied immediately here rather than only once the debounced check answers, since this one is a network
    /// call and can take long enough that a stale confirmation sitting under a row the operator just changed would
    /// read as still true. Set from outside by <see cref="ProjectDialogViewModel"/> once its own check completes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfirmedReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotFoundReachable))]
    [NotifyPropertyChangedFor(nameof(IsNotSignedIn))]
    [NotifyPropertyChangedFor(nameof(IsCheckFailed))]
    private ProjectMemorySourceReachability? _reachability;

    /// <summary>
    /// The text to show under a <see cref="ProjectMemorySourceReachability.Confirmed"/> or (AC-499)
    /// <see cref="ProjectMemorySourceReachability.CheckFailed"/> row — the plugin's own
    /// <see cref="ProjectMemorySourceReachabilityResult.Detail"/>, or null to fall back to each state's own fixed
    /// sentence. Ignored for <see cref="ProjectMemorySourceReachability.NotSignedIn"/>/<see cref="ProjectMemorySourceReachability.NotFound"/>,
    /// the same restraint that result type's own doc comment describes.
    /// </summary>
    [ObservableProperty]
    private string? _reachabilityDetail;

    /// <summary>
    /// Whether this is the last row in <see cref="ProjectDialogViewModel.ResourceRows"/> — set from outside, by the
    /// dialog, whenever a row is added or removed (AC-485 review, FIX 8). Gates the row's own bottom divider in the
    /// view: the last row has no next row to separate itself from, so it must draw no line at all. Kept as a plain
    /// property set explicitly rather than answered by a binding that reaches back into the dialog's own
    /// <c>ResourceRows</c> list — that binding was tried first and measured not to re-run reliably when a row was
    /// added or removed (<c>ResourceRows</c> itself is the same collection reference for the dialog's whole
    /// lifetime, so nothing about it changing triggers a re-bind on its own), where a plain property the dialog sets
    /// explicitly cannot go stale the same way.
    /// </summary>
    [ObservableProperty]
    private bool _isLastRow;

    /// <summary>The choices offered for this row's memory source — the same list, shared, for every row (AC-166).</summary>
    public ObservableCollection<MemorySourceChoice> MemorySourceChoices { get; }

    /// <summary>
    /// Every family's own instances, shared for every row the same way <see cref="MemorySourceChoices"/> is (AC-499)
    /// — keyed the same case-insensitive way <see cref="ProjectMemorySourceFamily.Key"/> is matched. Not readonly
    /// (AC-523): <see cref="UpdateFamilyInstanceChoices"/> swaps this for a freshly rebuilt dictionary once the
    /// "Servers…" flow's own settings screen may have added or removed an instance, rather than this row staying
    /// pinned to whatever <see cref="ProjectDialogViewModel.CreateAsync"/> handed it when the dialog first opened.
    /// </summary>
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
        _sendsContent = sendsContent;
    }

    /// <summary>
    /// Swaps in a freshly rebuilt family-instances dictionary (AC-523) — called by
    /// <see cref="ProjectDialogViewModel.ConfigureMemorySourceAsync"/> once its own "Servers…" call returns, so a
    /// connection added or removed in the settings screen that call opened shows up here without the operator having
    /// to close and reopen the whole project dialog.
    /// <para>
    /// <see cref="SelectedFamilyInstance"/> is re-matched by <see cref="MemorySourceChoice.Scheme"/> against the new
    /// dictionary rather than left as-is: the old instance object is never in the new dictionary (each rebuild
    /// constructs fresh <see cref="MemorySourceChoice"/> records), so a reference-equality read would read every
    /// previously-selected instance as gone. A scheme still offered keeps its selection (AC-523 criterion 2); a
    /// scheme no longer offered — the operator removed that connection while the settings screen was open — falls
    /// back to no selection rather than silently keep pointing at something gone (AC-523 criterion 3).
    /// </para>
    /// </summary>
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

    /// <summary>
    /// Keeps a role switch from silently changing what <see cref="Reference"/> means (AC-485 review, MUST-FIX 1).
    /// <see cref="ShowsMemorySourcePicker"/> hides the source picker the instant <see cref="Role"/> stops being
    /// <see cref="ProjectResourceRole.Memory"/> — but before this method existed, the scheme that picker had folded
    /// away stayed folded: switching a row from Memory (source "Depot project", box showing "cockpit") to Reference
    /// left the box still showing the bare value while <see cref="ToDomain"/> kept saving <c>depot:cockpit</c>
    /// underneath — a reference to nothing, with nothing on screen telling the operator it had changed. Switching
    /// back to Memory happened to repair it, which is what made the bug easy to miss: only a role change that
    /// <em>stuck</em> actually lost anything.
    /// <para>
    /// Both directions are handled, mirroring <c>ProjectDialogViewModel.CreateAsync</c>'s own load-time fold/unfold
    /// of a saved reference so a row behaves the same whether the shape it is given came from disk or from the
    /// operator switching roles just now:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// Away from Memory, with a source other than Folder picked: the scheme is folded into <see cref="Reference"/>
    /// right now — the box changes in front of the operator instead of the change happening silently at save — and
    /// the picker's selection is dropped back to Folder (or cleared, when there is no picker), since the picker is
    /// about to disappear and nothing should be left pointing at a source the box no longer names.
    /// </description></item>
    /// <item><description>
    /// Onto Memory, with the box holding a <c>&lt;scheme&gt;:&lt;value&gt;</c> reference to a source this dialog
    /// actually offers: that source is selected and the box is set to the bare value, exactly as a freshly loaded
    /// row would show it. Anything else — a plain path, an unrecognised scheme — leaves Folder selected and the box
    /// untouched, the same "leave it alone rather than guess" rule <c>CreateAsync</c> already applies.
    /// </description></item>
    /// </list>
    /// </summary>
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

    /// <summary>AC-503: a Reachability answer belongs to a specific typed value — an edit invalidates it the instant it happens, before any debounced re-check can even start.</summary>
    partial void OnReferenceChanged(string value) => _ResetReachability();

    /// <summary>
    /// AC-503: a Reachability answer belongs to a specific source — picking a different one (or Folder) invalidates
    /// whatever the previous source found. AC-499: also resets the instance axis to the newly picked family's own
    /// first instance (or null, if it has none, or the new top choice is not a family at all) — a stale instance
    /// pointing at the previous family's own connections must not survive the top choice changing out from under it.
    /// A caller that then wants a specific instance (loading a stored reference, a role switch back onto Memory)
    /// overwrites this immediately after, the same "default then overwrite" order <c>CreateAsync</c>'s own Folder
    /// preselection already uses.
    /// </summary>
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

    /// <summary>AC-503/AC-499: a Reachability answer belongs to a specific instance too — switching within a family invalidates whatever the previous instance found, the same reason switching the top choice does.</summary>
    partial void OnSelectedFamilyInstanceChanged(MemorySourceChoice? value) => _ResetReachability();

    private void _ResetReachability()
    {
        Reachability = null;
        ReachabilityDetail = null;
    }

    /// <summary>
    /// Finds which top-level choice and, if it is a family, which of its instances a saved <paramref name="scheme"/>
    /// names (AC-499) — one layer deeper than the pre-AC-499 search that only ever looked at
    /// <see cref="MemorySourceChoices"/> itself. Shared by <c>ProjectDialogViewModel.CreateAsync</c> (loading a
    /// stored reference) and <see cref="OnRoleChanged"/> (a role switch back onto Memory with a scheme already
    /// typed) rather than duplicated in both — the same search either way.
    /// </summary>
    /// <param name="top">The top-level choice to select, or null when nothing matches.</param>
    /// <param name="instance">The family instance to select alongside <paramref name="top"/>, or null when <paramref name="top"/> is an ungrouped source (or nothing matched at all).</param>
    /// <returns>True when <paramref name="scheme"/> names a source this row can actually offer.</returns>
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

    /// <summary>
    /// Whether the memory-source picker is shown for this row — only for a Memory row, and only once something
    /// registered a source (AC-166's <c>HasMemorySources</c>, answered per row instead of once for the dialog).
    /// </summary>
    public bool ShowsMemorySourcePicker => Role == ProjectResourceRole.Memory && MemorySourceChoices.Count > 0;

    /// <summary>
    /// Whether to show a confirmation under this Memory row (AC-503) — the counterpart to <see cref="IsBroken"/> for
    /// a plugin-registered source rather than a filesystem path. Gated on <see cref="Role"/> and a non-blank typed
    /// value the same way <see cref="IsNotFoundReachable"/>/<see cref="IsNotSignedIn"/>/<see cref="IsCheckFailed"/>
    /// are, so a blank field never shows any of the four (AC-503 acceptance criterion 6, AC-499) whatever
    /// <see cref="Reachability"/> last held from before the field was cleared — clearing the field itself already
    /// resets it (see <see cref="OnReferenceChanged"/>), but this gate is the belt to that braces.
    /// </summary>
    public bool IsConfirmedReachable =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.Confirmed;

    /// <summary>The AC-503 "not found" state — see <see cref="IsConfirmedReachable"/>'s own remarks on the shared gating.</summary>
    public bool IsNotFoundReachable =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.NotFound;

    /// <summary>The AC-503 "not signed in" state — see <see cref="IsConfirmedReachable"/>'s own remarks on the shared gating.</summary>
    public bool IsNotSignedIn =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.NotSignedIn;

    /// <summary>The AC-499 "the check itself failed" state — see <see cref="IsConfirmedReachable"/>'s own remarks on the shared gating.</summary>
    public bool IsCheckFailed =>
        Role == ProjectResourceRole.Memory && !string.IsNullOrWhiteSpace(Reference) && Reachability == ProjectMemorySourceReachability.CheckFailed;

    /// <summary>
    /// Whether "Send along" is offered for this row at all (AC-486) — <see cref="ProjectResourceRole.Instructions"/>
    /// alone, the same per-role gating idiom <see cref="ShowsMemorySourcePicker"/> already uses. See
    /// <see cref="ProjectResource.SendsContent"/>'s own doc comment for why the other two roles never offer it: a
    /// memory row is read and written back to all session long and far too large to inline, and a reference row
    /// exists to be looked up, not read up front.
    /// </summary>
    public bool ShowsSendsContentOption => Role == ProjectResourceRole.Instructions;

    /// <summary>
    /// Whether <see cref="Reference"/> holds a folder path rather than a source's bare value — gates "Choose…" for a
    /// Memory row the same way the single Memory row used to. AC-499: a family is never folder mode, even with no
    /// instance picked yet (nothing has ever been chosen to browse a folder in place of) — <see cref="SelectedMemorySourceLeaf"/>
    /// being null in that state is "nothing to fold a scheme from", not "this means a folder".
    /// </summary>
    public bool IsMemoryFolderMode =>
        SelectedMemorySourceChoice is not { } choice
        || (choice.Scheme is not { Length: > 0 } && choice.FamilyKey is not { Length: > 0 });

    /// <summary>
    /// The choice this row actually means right now, whichever axis carries it (AC-499): the top choice itself for
    /// "Folder" or an ungrouped source, <see cref="SelectedFamilyInstance"/> when the top choice is a family (null
    /// when that family has no instance picked). What <see cref="ToDomain"/> folds a scheme from, what
    /// <see cref="ProjectDialogViewModel"/>'s reachability check calls, and what <c>ProjectDialog</c>'s own "Choose…"
    /// handler opens a location picker with.
    /// </summary>
    public MemorySourceChoice? SelectedMemorySourceLeaf =>
        SelectedMemorySourceChoice?.FamilyKey is not null ? SelectedFamilyInstance : SelectedMemorySourceChoice;

    /// <summary>
    /// Whether "Choose…" does anything at all. A Memory row with a source other than Folder picked used to always
    /// take a typed identifier, not a path — the same reason the single Memory row's own button used to go
    /// insensitive. AC-502 narrows that: it stays insensitive only for a source that cannot enumerate its own
    /// locations (<see cref="MemorySourceChoice.ListLocationsAsync"/> null); one that can opens a picker of names
    /// instead of a folder browser. Every other role always browses (for a file — see <see cref="ReferencePlaceholder"/>).
    /// AC-499: a family with no instance picked has no <see cref="SelectedMemorySourceLeaf"/> to ask, so this reads
    /// false the same as a source with no picker — nothing to browse until an instance exists to browse it with.
    /// </summary>
    public bool CanBrowse =>
        Role != ProjectResourceRole.Memory
        || IsMemoryFolderMode
        || SelectedMemorySourceLeaf?.ListLocationsAsync is not null;

    /// <summary>
    /// Whether the server row — the instance dropdown (or its empty-state hint) plus "Servers…" — is shown under
    /// this row's own source picker (AC-499). Only when the top choice is a family: an ungrouped source or Folder
    /// has no second axis to pick from at all.
    /// </summary>
    public bool ShowsMemorySourceServerRow => Role == ProjectResourceRole.Memory && SelectedMemorySourceChoice?.FamilyKey is not null;

    /// <summary>The picked family's own instances (AC-499) — empty when the top choice is not a family, or is a family with nothing registered under it yet.</summary>
    public IReadOnlyList<MemorySourceChoice> FamilyInstanceChoices =>
        SelectedMemorySourceChoice?.FamilyKey is { } familyKey && _familyInstanceChoicesByKey.TryGetValue(familyKey, out var instances)
            ? instances
            : [];

    /// <summary>Whether the picked family actually has an instance to offer — gates showing the instance dropdown itself rather than <see cref="MemorySourceInstanceEmptyHint"/> in its place.</summary>
    public bool HasFamilyInstances => FamilyInstanceChoices.Count > 0;

    /// <summary>What the server row shows in place of the instance dropdown when the picked family has none (AC-499) — the family's own <see cref="ProjectMemorySourceFamily.EmptyHint"/>, or a generic fallback for a family that never set one.</summary>
    public string? MemorySourceInstanceEmptyHint => SelectedMemorySourceChoice?.EmptyHint ?? "No server configured yet.";

    /// <summary>Whether the server row's "Servers…" button does anything at all (AC-499) — never a dead button, the same rule <see cref="CanBrowse"/> already follows for "Choose…".</summary>
    public bool CanConfigureMemorySource => SelectedMemorySourceChoice?.ConfigureAsync is not null;

    /// <summary>
    /// The line under a Memory row's picker (AC-166): stops calling the location a folder once a source other than
    /// Folder is picked — the same reasoning the single Memory row's own hint carried before this row replaced it.
    /// AC-499: a family reads as "not a folder" the instant it is picked, whether or not an instance has been chosen
    /// under it yet — the operator already committed to typing an identifier, not browsing a folder.
    /// </summary>
    public string MemoryHint =>
        !IsMemoryFolderMode
            ? "Where this project's memory lives — the name it goes by in the source above, not a path. Sessions are told about it, so they can look things up instead of being told again."
            : "Where this project's memory lives — a folder, kept apart from the source folder. Sessions are told about it, so they can look things up instead of being told again.";

    /// <summary>
    /// What the reference box hints at: a folder or identifier for a Memory row (mirroring the old single row), a
    /// plain file/folder hint for the other two roles. AC-499: names the picked instance where there is one, falling
    /// back to the family's own label when a family is picked but no instance has been chosen yet.
    /// </summary>
    public string ReferencePlaceholder =>
        Role switch
        {
            ProjectResourceRole.Memory when !IsMemoryFolderMode =>
                $"An identifier {SelectedMemorySourceLeaf?.Label ?? SelectedMemorySourceChoice?.Label} understands",
            ProjectResourceRole.Memory => "No memory location",
            _ => "A file or folder path",
        };

    /// <summary>Whether this row has neither a reference nor a label — the same "untouched" shape <see cref="ProjectInfoFieldViewModel"/> drops on save.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(Reference) && string.IsNullOrWhiteSpace(Label);

    /// <summary>
    /// This row as the domain model that is actually saved: the scheme folded into the reference when a Memory row
    /// has a source other than Folder picked (AC-166) — the same fold <c>ProjectDialogViewModel._ToMemoryRef</c>
    /// used to do once for the whole dialog, now done per row. A blank value under a picked scheme saves as no
    /// reference at all (matching <c>_ToMemoryRef</c>'s own rule) rather than a bare <c>"{scheme}:"</c> that names a
    /// source and nothing in it. AC-499: the scheme comes from <see cref="SelectedMemorySourceLeaf"/> — a family
    /// with no instance picked has none to fold, so the typed text saves unprefixed, the same as Folder, rather than
    /// inventing a scheme that names nothing.
    /// </summary>
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

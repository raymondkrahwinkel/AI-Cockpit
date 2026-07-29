using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

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
    private ProjectResourceRole _role;

    [ObservableProperty]
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
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMemoryFolderMode))]
    [NotifyPropertyChangedFor(nameof(MemoryHint))]
    [NotifyPropertyChangedFor(nameof(ReferencePlaceholder))]
    [NotifyPropertyChangedFor(nameof(CanBrowse))]
    private MemorySourceChoice? _selectedMemorySourceChoice;

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

    public ProjectResourceRowViewModel(
        ObservableCollection<MemorySourceChoice> memorySourceChoices,
        ProjectResourceRole role = ProjectResourceRole.Memory,
        string reference = "",
        string label = "",
        bool reachesSessions = true,
        bool sendsContent = false)
    {
        MemorySourceChoices = memorySourceChoices;
        _role = role;
        _reference = reference;
        _label = label;
        _reachesSessions = reachesSessions;
        _sendsContent = sendsContent;
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
        // AC-486: leaving Instructions must not leave "Send along" quietly ticked on a row where it now means
        // nothing — the checkbox is about to disappear (see ShowsSendsContentOption below), and nothing reads this
        // flag for any other role, so there is nothing to fold anywhere the way a Memory row's picked scheme is;
        // simply switching it back off is the whole fix.
        // AC-486: leaving Instructions must not leave "Send along" quietly ticked on a row where it now means
        // nothing — the checkbox is about to disappear (see ShowsSendsContentOption below), and nothing reads this
        // flag for any other role, so there is nothing to fold anywhere the way a Memory row's picked scheme is;
        // simply switching it back off is the whole fix.
        // AC-486: leaving Instructions must not leave "Send along" quietly ticked on a row where it now means
        // nothing — the checkbox is about to disappear (see ShowsSendsContentOption below), and nothing reads this
        // flag for any other role, so there is nothing to fold anywhere the way a Memory row's picked scheme is;
        // simply switching it back off is the whole fix.
        if (oldValue == ProjectResourceRole.Instructions && newValue != ProjectResourceRole.Instructions)
        {
            SendsContent = false;
        }

        if (oldValue == ProjectResourceRole.Memory && newValue != ProjectResourceRole.Memory)
        {
            if (SelectedMemorySourceChoice is { Scheme.Length: > 0 } choice)
            {
                var typed = Reference.Trim();
                Reference = typed.Length > 0 ? $"{choice.Scheme}:{typed}" : string.Empty;
            }

            SelectedMemorySourceChoice = MemorySourceChoices.Count > 0 ? MemorySourceChoices[0] : null;
            return;
        }

        if (newValue == ProjectResourceRole.Memory && oldValue != ProjectResourceRole.Memory)
        {
            if (MemorySourceChoices.Count > 0
                && ProjectMemoryRef.TryParse(Reference, out var scheme, out var value)
                && MemorySourceChoices.FirstOrDefault(candidate =>
                    candidate.Scheme is { } registered && string.Equals(registered, scheme, StringComparison.OrdinalIgnoreCase)) is { } matched)
            {
                SelectedMemorySourceChoice = matched;
                Reference = value;
            }
            else if (MemorySourceChoices.Count > 0)
            {
                SelectedMemorySourceChoice = MemorySourceChoices[0];
            }
        }
    }

    /// <summary>
    /// Whether the memory-source picker is shown for this row — only for a Memory row, and only once something
    /// registered a source (AC-166's <c>HasMemorySources</c>, answered per row instead of once for the dialog).
    /// </summary>
    public bool ShowsMemorySourcePicker => Role == ProjectResourceRole.Memory && MemorySourceChoices.Count > 0;

    /// <summary>
    /// Whether "Send along" is offered for this row at all (AC-486) — <see cref="ProjectResourceRole.Instructions"/>
    /// alone, the same per-role gating idiom <see cref="ShowsMemorySourcePicker"/> already uses. See
    /// <see cref="ProjectResource.SendsContent"/>'s own doc comment for why the other two roles never offer it: a
    /// memory row is read and written back to all session long and far too large to inline, and a reference row
    /// exists to be looked up, not read up front.
    /// </summary>
    public bool ShowsSendsContentOption => Role == ProjectResourceRole.Instructions;

    /// <summary>Whether <see cref="Reference"/> holds a folder path rather than a source's bare value — gates "Choose…" for a Memory row the same way the single Memory row used to.</summary>
    public bool IsMemoryFolderMode => SelectedMemorySourceChoice?.Scheme is null;

    /// <summary>
    /// Whether "Choose…" browses for anything at all. A Memory row with a source other than Folder picked takes a
    /// typed identifier, not a path — the same reason the single Memory row's own button used to go insensitive.
    /// Every other role always browses (for a file — see <see cref="ReferencePlaceholder"/>).
    /// </summary>
    public bool CanBrowse => Role != ProjectResourceRole.Memory || IsMemoryFolderMode;

    /// <summary>
    /// The line under a Memory row's picker (AC-166): stops calling the location a folder once a source other than
    /// Folder is picked — the same reasoning the single Memory row's own hint carried before this row replaced it.
    /// </summary>
    public string MemoryHint =>
        SelectedMemorySourceChoice is { Scheme.Length: > 0 }
            ? "Where this project's memory lives — the name it goes by in the source above, not a path. Sessions are told about it, so they can look things up instead of being told again."
            : "Where this project's memory lives — a folder, kept apart from the source folder. Sessions are told about it, so they can look things up instead of being told again.";

    /// <summary>What the reference box hints at: a folder or identifier for a Memory row (mirroring the old single row), a plain file/folder hint for the other two roles.</summary>
    public string ReferencePlaceholder =>
        Role switch
        {
            ProjectResourceRole.Memory when SelectedMemorySourceChoice is { Scheme.Length: > 0 } choice => $"An identifier {choice.Label} understands",
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
    /// source and nothing in it.
    /// </summary>
    public ProjectResource ToDomain()
    {
        var typed = Reference.Trim();
        var reference = ShowsMemorySourcePicker && SelectedMemorySourceChoice is { Scheme.Length: > 0 } choice
            ? typed.Length > 0 ? $"{choice.Scheme}:{typed}" : string.Empty
            : typed;

        return new ProjectResource(reference, Role)
        {
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            ReachesSessions = ReachesSessions,
            SendsContent = SendsContent,
        };
    }
}

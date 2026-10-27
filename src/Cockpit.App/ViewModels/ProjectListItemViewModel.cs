using System.Globalization;
using System.Windows.Input;
using Cockpit.App.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Projects;

namespace Cockpit.App.ViewModels;

// AC-1600: one B2 row — a project on this machine or a team project still to add. Observable for `IsSelected` only,
// which the list keeps in step with `ProjectsViewModel.SelectedListItem` without rebuilding the row.
public sealed partial class ProjectListItemViewModel : ObservableObject
{
    public ProjectListItemViewModel(
        Project project, int sessionsRunning, string? role = null, bool isInOrganization = false,
        bool hasRemoteChanges = false, ProjectCardActions? actions = null)
    {
        Project = project;
        Id = project.Id;
        Name = project.Name;
        Description = project.Description;
        SessionsRunning = sessionsRunning;
        Role = role;
        IsInOrganization = isInOrganization;
        HasRemoteChanges = hasRemoteChanges;
        Actions = actions;
    }

    public ProjectListItemViewModel(SharedProjectOffer sharedProject)
    {
        SharedProject = sharedProject;
        Id = sharedProject.Id;
        Name = sharedProject.Name;
        Description = sharedProject.Unavailable ?? sharedProject.Description;
        Role = sharedProject.Role;
        IsInOrganization = true;
    }

    public Project? Project { get; }

    public SharedProjectOffer? SharedProject { get; }

    public string Id { get; }

    public string Name { get; }

    public string? Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public string DescriptionText => Description is { } description && !string.IsNullOrWhiteSpace(description)
        ? description
        : "No description";

    public string? Category => Project?.Category;

    public int SessionsRunning { get; }

    public bool IsOnThisMachine => Project is not null;

    public string? Role { get; }

    public bool HasRole => Role is { Length: > 0 };

    public bool IsInOrganization { get; }

    public bool HasRemoteChanges { get; }

    public ProjectCardActions? Actions { get; }

    // What "Add…" runs for a team project: `ProjectsViewModel.FinishSettingUpCommand`, set when the row is built.
    public ICommand? Add { get; init; }

    public bool IsSyncable => IsOnThisMachine && IsInOrganization;

    // The detail pane's origin: "◆ Synvolution" on the pill, "◆ depot.example.com / synvolution" under Details. Set by
    // `ProjectsViewModel` from the row's section; host-generic, so no source's brand is named here (AC-244).
    public string Origin { get; init; } = "● This machine";

    public string WhereItLives { get; init; } = "● This machine only";

    public string CategoryText => Category is { Length: > 0 } category ? category : "Uncategorized";

    public string? Folder => Project?.SourceDirectory;

    [ObservableProperty]
    private bool _isSelected;

    public bool HasProfile => Project?.DefaultProfileLabel is { Length: > 0 };

    public bool CanStart => HasProfile;

    public bool NeedsProfile => IsOnThisMachine && !HasProfile;

    public bool CanAdd => SharedProject?.CanAdd == true;

    public string RunsOn => Project switch
    {
        null => "Not on this machine",
        { DefaultProfileLabel: { Length: > 0 } profile } => $"▸ {profile}",
        _ => "No profile yet",
    };

    public string RunsOnDetail => Project switch
    {
        null => "Not on this machine yet",
        { DefaultProfileLabel: { Length: > 0 } profile } => profile,
        _ => "No profile yet",
    };

    // The small list's one meta line under the name (mockup `.c-narrowmeta`).
    public string RunsOnAndLastOpened => IsOnThisMachine ? $"{RunsOn} · {LastOpened}" : "Not on this machine yet";

    public string LastOpened
    {
        get
        {
            if (Project is not { } project)
            {
                return "—";
            }

            // The overview's own relative wording, without its "Opened" verb (an empty verb, trimmed).
            return project.LastOpenedAt is { } openedAt
                && LastOpenedConverter.Instance.Convert(openedAt, typeof(string), string.Empty, CultureInfo.InvariantCulture) is string text
                ? text.Trim()
                : "never opened";
        }
    }

    public bool IsRunning => SessionsRunning > 0;

    public string RunningLabel => $"● {SessionsRunning} running";

    public bool HasJobs => Project?.Jobs.Count > 0;

    public string JobsLabel => Project?.Jobs is [{ Recurrence: { } recurrence }]
        ? $"⏱ 1 job · {recurrence.Describe()}"
        : $"⏱ {Project?.Jobs.Count} {(Project?.Jobs.Count == 1 ? "job" : "jobs")}";

    // The quiet line when nothing else is said: a bound project is current, a local one has no other copy.
    public bool ShowsQuietStatus => IsOnThisMachine && !HasRemoteChanges && !IsRunning && !HasJobs;

    public string QuietStatus => IsInOrganization ? "✓ Up to date" : "Only on this machine";

    public string CompactStatus => CanAdd ? "Ready to add"
        : HasRemoteChanges ? "Changed on Depot"
        : IsRunning ? RunningLabel
        : HasJobs ? JobsLabel
        : QuietStatus;
}

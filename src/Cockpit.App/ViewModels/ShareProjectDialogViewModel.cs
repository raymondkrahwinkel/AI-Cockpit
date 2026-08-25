using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cockpit.Core.Projects;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App.ViewModels;

// AC-620's confirmation screen — the moment a not-yet-shared local project first leaves for Depot. No field-level
// opt-out (Raymond's decision): the portable/local split is a property of the field. GoesToDepot/StaysOnThisMachine
// mirror PublishAsync's own write-time classification, including a machine-scope row's split (name travels as a Placeholder, path stays).
public partial class ShareProjectDialogViewModel : ViewModelBase
{
    private readonly Project _project;
    private readonly IReadOnlyList<ISharedProjectSource> _sources;

    // Raised when the dialog is done: the project with its new binding row, or null when the operator cancelled or
    // the publish never succeeded (an error stays visible in the dialog instead of closing it).
    public event Action<Project?>? CloseRequested;

    // Design-time constructor for the Avalonia previewer.
    public ShareProjectDialogViewModel()
    {
        _project = Project.Create("PayrollProcessor") with
        {
            Description = "Loonverwerking — DDD-template, MSSQL.",
            GitUrl = "git@github.com:synvolution/payroll-processor.git",
            BehaviorPrompt = "Werk volgens de DDD-template.",
            Assistant = "Aura",
            SourceDirectories = [new("/home/raymond/RiderProjects/payroll")],
            DefaultProfileLabel = "Claude — Opus 5",
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["Depot: Work", "YouTrack"] },
            Category = "Synvolution",
            Resources =
            [
                new ProjectResource("docs/CONVENTIONS.md", ProjectResourceRole.Reference) { Label = "Conventions" },
                new ProjectResource("~/Notes/payroll.md", ProjectResourceRole.Memory) { Label = "Personal notes" },
                // AC-699: unlabelled, so it falls back to its role for a name — the shape that showed one row
                // under the same "Memory" label in both columns.
                new ProjectResource("/home/raymond/Nextcloud/Memory/Payroll/", ProjectResourceRole.Memory),
                new ProjectResource("/home/raymond/dumps/payroll-2026.sql", ProjectResourceRole.Reference) { Label = "Testdata dump" },
            ],
        };
        _sources = [];
        ProjectName = _project.Name;
        _BuildFieldRows();
        Connections.Add("Work — depot.krahwinkel-it.nl");
        SelectedConnectionIndex = 0;
        Targets.Add(new SharedProjectPublishTarget("depot:payroll-processor", "payroll-processor", "Owner"));
        SelectedTarget = Targets[0];
    }

    private ShareProjectDialogViewModel(Project project, IReadOnlyList<ISharedProjectSource> sources)
    {
        _project = project;
        _sources = sources;
        ProjectName = project.Name;
        _BuildFieldRows();

        foreach (var source in sources)
        {
            Connections.Add(source.SourceName);
        }

        if (Connections.Count > 0)
        {
            SelectedConnectionIndex = 0;
        }
    }

    public static ShareProjectDialogViewModel Create(Project project, IReadOnlyList<ISharedProjectSource> sources) =>
        new(project, sources);

    public string ProjectName { get; }

    public string DialogTitle => $"Share via Depot — {ProjectName}";

    public ObservableCollection<string> Connections { get; } = [];

    [ObservableProperty]
    private int _selectedConnectionIndex = -1;

    private ISharedProjectSource? _SelectedSource =>
        SelectedConnectionIndex >= 0 && SelectedConnectionIndex < _sources.Count ? _sources[SelectedConnectionIndex] : null;

    partial void OnSelectedConnectionIndexChanged(int value) => _ = _LoadTargetsAsync();

    public ObservableCollection<SharedProjectPublishTarget> Targets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RoleNote))]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand))]
    private SharedProjectPublishTarget? _selectedTarget;

    [ObservableProperty]
    private bool _isLoadingTargets;

    [ObservableProperty]
    private string? _loadError;

    public string? RoleNote => SelectedTarget?.Role is { Length: > 0 } role
        ? $"You are {role} — you may publish here."
        : null;

    public ObservableCollection<ShareFieldRowViewModel> GoesToDepot { get; } = [];

    public ObservableCollection<ShareFieldRowViewModel> StaysOnThisMachine { get; } = [];

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ShareCommand))]
    private bool _isSharing;

    private bool _CanShare => SelectedTarget is not null && !IsSharing;

    private async Task _LoadTargetsAsync()
    {
        Targets.Clear();
        SelectedTarget = null;
        LoadError = null;

        if (_SelectedSource is not { } source)
        {
            return;
        }

        IsLoadingTargets = true;
        try
        {
            var result = await source.ListPublishTargetsAsync(CancellationToken.None).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                LoadError = result.Error is { Length: > 0 } error ? error : "Could not list this connection's projects.";
                return;
            }

            foreach (var target in result.Targets.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase))
            {
                Targets.Add(target);
            }

            SelectedTarget = Targets.Count > 0 ? Targets[0] : null;

            // AC-699: a succeeded call with nothing in it is the state that has to say something. A connection
            // only offers projects the operator may write to, so "empty" reads as a broken dropdown otherwise —
            // which is exactly how the role-parsing bug behind this ticket stayed invisible.
            if (Targets.Count == 0)
            {
                LoadError = "This connection has no project you can publish to — you need Editor rights or better on one.";
            }
        }
        catch (Exception exception)
        {
            // Nothing awaits this method (it is started from a property change), so an exception escaping here is a
            // silently empty dropdown rather than a crash. Shown instead, same place a failed result lands.
            LoadError = $"Could not list this connection's projects: {exception.Message}";
        }
        finally
        {
            IsLoadingTargets = false;
        }
    }

    // Mirrors PublishAsync's own write-time filter, computed here purely for display. AdditionalInfo is never
    // shown: it is not part of the portable contract yet (CockpitProjectDefinitionSecrecyTests pins that on the write side).
    private void _BuildFieldRows()
    {
        GoesToDepot.Add(new ShareFieldRowViewModel("Name", _project.Name));

        if (_project.Description is { Length: > 0 } description)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel("Description", description));
        }

        if (_project.GitUrl is { Length: > 0 } gitUrl)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel("Git source", gitUrl));
        }

        if (_project.BehaviorPrompt is { Length: > 0 } behaviorPrompt)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel("Behaviour", behaviorPrompt));
        }

        // AC-699: a project that narrowed its servers to none still writes that restriction (an empty Enabled list
        // is a choice, only a null overlay is "no opinion") — so this says so instead of showing no row at all.
        if (_project.McpOverlay.EnabledServerNames is { } enabledServerNames)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel(
                "MCP servers", enabledServerNames.Count > 0 ? string.Join(" · ", enabledServerNames) : "none — every server starts unticked"));
        }

        GoesToDepot.Add(new ShareFieldRowViewModel("Worktree isolation", _project.IsolateInWorktreeByDefault ? "on" : "off"));

        StaysOnThisMachine.Add(new ShareFieldRowViewModel("Profile", _project.DefaultProfileLabel is { Length: > 0 } profile ? profile : "not set"));

        // AC-1071: named even when unset, beside the profile it belongs with — this column exists so the operator
        // can see that whoever binds this project keeps their own assistant rather than inheriting theirs.
        StaysOnThisMachine.Add(new ShareFieldRowViewModel(
            "Assistant", _project.Assistant is { Length: > 0 } assistant ? assistant : "not set"));

        if (_project.SourceDirectory is { Length: > 0 } folder)
        {
            StaysOnThisMachine.Add(new ShareFieldRowViewModel("Folder", folder));
        }

        // AC-699: the fields PublishAsync has nowhere to put — named here rather than left out, since a column
        // titled "what stays on this machine" that quietly skips half of what stays is the same kind of wrong.
        if (_project.Category is { Length: > 0 } category)
        {
            StaysOnThisMachine.Add(new ShareFieldRowViewModel("Category", category));
        }

        // AC-763: the logo now travels with the rest of the definition — see _BuildPublishDefinitionAsync.
        if (_project.LogoPath is { Length: > 0 } logoPath)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel("Logo", logoPath));
        }

        if (_project.HasAdditionalInfo)
        {
            StaysOnThisMachine.Add(new ShareFieldRowViewModel(
                "Anything else worth keeping", $"{_project.AdditionalInfo.Count} row(s) — never part of a shared project"));
        }

        if (_project.PluginFields.Count > 0)
        {
            StaysOnThisMachine.Add(new ShareFieldRowViewModel("Where it is tracked", string.Join(" · ", _project.PluginFields.Keys)));
        }

        foreach (var resource in _project.Resources)
        {
            var label = resource.Label is { Length: > 0 } ? resource.Label : resource.Role.ToString();

            if (ProjectResourceSecretPathHeuristic.IsLikelySecretPath(resource.Reference))
            {
                StaysOnThisMachine.Add(new ShareFieldRowViewModel(label, "secret — never sent to Depot"));
                continue;
            }

            if (ProjectResourcePathPortability.ClassifyScope(resource.Reference) == ProjectResourceScope.Machine)
            {
                // CockpitProjectResourceEntry.Create writes this as a Placeholder: the role/label still cross to
                // Depot (a colleague sees the row when they bind), only the path itself does not. AC-699: the two
                // halves say so in their own labels — one row's name repeated in both columns read as a duplicate.
                GoesToDepot.Add(new ShareFieldRowViewModel($"{label} — name only", "the path itself is filled in on each machine"));
                StaysOnThisMachine.Add(new ShareFieldRowViewModel($"{label} — path", resource.Reference));
            }
            else
            {
                GoesToDepot.Add(new ShareFieldRowViewModel(label, resource.Reference));
            }
        }
    }

    [RelayCommand(CanExecute = nameof(_CanShare))]
    private async Task ShareAsync()
    {
        if (_SelectedSource is not { } source || SelectedTarget is not { } target)
        {
            return;
        }

        IsSharing = true;
        ErrorMessage = null;
        try
        {
            var definition = await _BuildPublishDefinitionAsync().ConfigureAwait(true);
            var result = await source.PublishAsync(target.Id, definition, CancellationToken.None).ConfigureAwait(true);

            if (result.Outcome != SharedProjectPublishOutcome.Success || result.BoundId is not { Length: > 0 } boundId)
            {
                ErrorMessage = result.Error is { Length: > 0 } error ? error : "Depot did not confirm the publish.";
                return;
            }

            // Prepended, same reason SharedProjectBindingDialogViewModel.ToProject prepends its own binding row: it
            // is what Project.MemoryRef/_ClaimBoundProjects resolve to, ahead of any resource already carried.
            // AC-762 bijvangst: replaces an existing Memory row instead of stacking a second one — sharing twice
            // used to leave a stale row behind that "Stop sharing" (which removes only the first match) never reached.
            var bound = _project with
            {
                Resources =
                [
                    new ProjectResource(boundId, ProjectResourceRole.Memory),
                    .. _project.Resources.Where(resource => resource.Role != ProjectResourceRole.Memory),
                ],
                // AC-762: the ◆ badge's fallback for a cold start — see Project.SharedSourceName.
                SharedSourceName = source.SourceName,
            };
            CloseRequested?.Invoke(bound);
        }
        finally
        {
            IsSharing = false;
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(null);

    // Every resource row, unfiltered — PublishAsync's own CockpitProjectResourceFilter is what decides, at write
    // time, which ones actually cross. Sending the same set _BuildFieldRows previewed (rather than pre-filtering
    // here too) keeps this one call the single place that decision is made.
    private async Task<SharedProjectPublishDefinition> _BuildPublishDefinitionAsync()
    {
        var resources = _project.Resources
            .Select(resource => new SharedProjectPublishResource(resource.Role.ToString(), resource.Reference, resource.Label))
            .ToList();

        return new SharedProjectPublishDefinition(
            _project.Name,
            _project.Description,
            _project.GitUrl,
            _project.BehaviorPrompt,
            _project.IsolateInWorktreeByDefault,
            _project.McpOverlay.EnabledServerNames,
            resources,
            await _ReadLogoBytesAsync().ConfigureAwait(true));
    }

    // AC-763: _project.LogoPath, when set, already names the cockpit's own stored copy (every save path runs it
    // through ProjectsViewModel._WithStoredLogoAsync first) — a plain local file, never a URL, so a direct read
    // is enough without repeating IProjectLogoStore's own remote-download/SVG-rasterise logic here.
    private async Task<byte[]?> _ReadLogoBytesAsync()
    {
        if (_project.LogoPath is not { Length: > 0 } path)
        {
            return null;
        }

        try
        {
            return await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // A logo is decoration (ProjectLogoStore.SaveAsync's own reasoning) — a stored copy that vanished
            // costs the picture, not the publish.
            return null;
        }
    }
}

// One row of ShareProjectDialogViewModel.GoesToDepot/StaysOnThisMachine — a field name and what it currently holds.
public sealed record ShareFieldRowViewModel(string Label, string Value);

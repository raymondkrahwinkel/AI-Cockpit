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
            SourceDirectory = "/home/raymond/RiderProjects/payroll",
            DefaultProfileLabel = "Claude — Opus 5",
            McpOverlay = new ProjectMcpOverlay { EnabledServerNames = ["Depot: Work", "YouTrack"] },
            Resources =
            [
                new ProjectResource("docs/CONVENTIONS.md", ProjectResourceRole.Reference) { Label = "Conventions" },
                new ProjectResource("~/Notes/payroll.md", ProjectResourceRole.Memory) { Label = "Personal notes" },
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

        if (_project.McpOverlay.EnabledServerNames is { Count: > 0 } enabledServerNames)
        {
            GoesToDepot.Add(new ShareFieldRowViewModel("MCP servers", string.Join(" · ", enabledServerNames)));
        }

        GoesToDepot.Add(new ShareFieldRowViewModel("Worktree isolation", _project.IsolateInWorktreeByDefault ? "on" : "off"));

        StaysOnThisMachine.Add(new ShareFieldRowViewModel("Profile", _project.DefaultProfileLabel is { Length: > 0 } profile ? profile : "not set"));

        if (_project.SourceDirectory is { Length: > 0 } folder)
        {
            StaysOnThisMachine.Add(new ShareFieldRowViewModel("Folder", folder));
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
                // Depot (a colleague sees the row when they bind), only the path itself does not.
                GoesToDepot.Add(new ShareFieldRowViewModel(label, "(name only — this machine's own path stays local)"));
                StaysOnThisMachine.Add(new ShareFieldRowViewModel(label, resource.Reference));
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
            var definition = _BuildPublishDefinition();
            var result = await source.PublishAsync(target.Id, definition, CancellationToken.None).ConfigureAwait(true);

            if (result.Outcome != SharedProjectPublishOutcome.Success || result.BoundId is not { Length: > 0 } boundId)
            {
                ErrorMessage = result.Error is { Length: > 0 } error ? error : "Depot did not confirm the publish.";
                return;
            }

            // Prepended, same reason SharedProjectBindingDialogViewModel.ToProject prepends its own binding row: it
            // is what Project.MemoryRef/_ClaimBoundProjects resolve to, ahead of any resource already carried.
            var bound = _project with
            {
                Resources = [new ProjectResource(boundId, ProjectResourceRole.Memory), .. _project.Resources],
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
    private SharedProjectPublishDefinition _BuildPublishDefinition()
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
            resources);
    }
}

// One row of ShareProjectDialogViewModel.GoesToDepot/StaysOnThisMachine — a field name and what it currently holds.
public sealed record ShareFieldRowViewModel(string Label, string Value);

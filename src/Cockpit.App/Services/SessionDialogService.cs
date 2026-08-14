using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Abstractions.WorkingPaths;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App.Services;

// Hosts the cockpit's dialogs. Constructs each dialog's view model with the profile store/login checker
// it injects, so the dialogs get their data without a service locator, then shows it and relays the
// typed result back to the caller.
//
// Two kinds, and the difference is deliberate (AC-367). A *surface* — projects, MCP servers, the
// plugin store, options — is something the operator works in for minutes, so it opens beside the cockpit
// through `SurfaceWindows` and leaves every running session reachable. A *question* —
// confirm a removal, type a password, trust a plugin — is answered in seconds and nothing may carry on
// half-answered, so it stays a modal `ShowDialog`. Either way the caller awaits the same task.
public sealed class SessionDialogService : ISessionDialogService, ISingletonService
{
    private readonly ISessionProfileStore _profileStore;
    private readonly IProfileLoginChecker _loginChecker;
    private readonly IProfileLoginStarter _loginStarter;
    private readonly IModelCatalog _modelCatalog;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly IMcpServerCatalog _mcpServerCatalog;
    private readonly IMcpToolTokenEstimator _tokenEstimator;
    private readonly IMcpOAuthCoordinator _oauthCoordinator;
    private readonly IReadOnlyList<ICockpitInternalMcpProvider> _internalMcpProviders;
    private readonly IPluginProviderRegistry _pluginProviderRegistry;
    private readonly IWorkingPathHistoryStore _workingPathStore;
    private readonly IConversationPickerRegistry _conversationPickers;
    private readonly DelegatedTasksViewModel _delegatedTasks;
    private readonly ITtySessionProviderResolver _ttyProviderResolver;
    private readonly IPluginTtyProviderRegistry _ttyProviderRegistry;
    private readonly IWorktreeManager _worktreeManager;
    private readonly IRepositoryCloneManager _cloneManager;
    private readonly IVerifyRunnerRegistry _verifyRunnerRegistry;
    private readonly IProjectStore _projectStore;
    private readonly IProjectFieldRegistry _projectFields;
    private readonly IProjectMemorySourceRegistry _memorySources;
    private readonly IProjectOwnershipRegistry _projectOwnership;
    private readonly SurfaceWindows _surfaces;

    // The assistant's own profile slot (AC-543) — its own section of the config, not an entry in `_profileStore`.
    private readonly IAssistantProfileStore _assistantProfileStore;

    public SessionDialogService(
        ISessionProfileStore profileStore,
        IProfileLoginChecker loginChecker,
        IModelCatalog modelCatalog,
        IMcpServerStore mcpServerStore,
        IMcpServerCatalog mcpServerCatalog,
        IMcpToolTokenEstimator tokenEstimator,
        IMcpOAuthCoordinator oauthCoordinator,
        IEnumerable<ICockpitInternalMcpProvider> internalMcpProviders,
        IPluginProviderRegistry pluginProviderRegistry,
        IWorkingPathHistoryStore workingPathStore,
        IConversationPickerRegistry conversationPickers,
        DelegatedTasksViewModel delegatedTasks,
        ITtySessionProviderResolver ttyProviderResolver,
        IPluginTtyProviderRegistry ttyProviderRegistry,
        IWorktreeManager worktreeManager,
        IRepositoryCloneManager cloneManager,
        IVerifyRunnerRegistry verifyRunnerRegistry,
        IProjectStore projectStore,
        IProjectFieldRegistry projectFields,
        IProjectMemorySourceRegistry memorySources,
        IProjectOwnershipRegistry projectOwnership,
        SurfaceWindows surfaces,
        IAssistantProfileStore assistantProfileStore,
        IProfileLoginStarter loginStarter)
    {
        _assistantProfileStore = assistantProfileStore;
        _surfaces = surfaces;
        _conversationPickers = conversationPickers;
        _delegatedTasks = delegatedTasks;
        _profileStore = profileStore;
        _loginChecker = loginChecker;
        _loginStarter = loginStarter;
        _modelCatalog = modelCatalog;
        _mcpServerStore = mcpServerStore;
        _mcpServerCatalog = mcpServerCatalog;
        _tokenEstimator = tokenEstimator;
        _oauthCoordinator = oauthCoordinator;
        _internalMcpProviders = [.. internalMcpProviders];
        _pluginProviderRegistry = pluginProviderRegistry;
        _workingPathStore = workingPathStore;
        _ttyProviderResolver = ttyProviderResolver;
        _ttyProviderRegistry = ttyProviderRegistry;
        _worktreeManager = worktreeManager;
        _cloneManager = cloneManager;
        _verifyRunnerRegistry = verifyRunnerRegistry;
        _projectStore = projectStore;
        _projectFields = projectFields;
        _memorySources = memorySources;
        _projectOwnership = projectOwnership;
    }

    public async Task<NewSessionResult?> ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, bool isolateInWorktree = false, Project? project = null)
    {
        // Topmost window, not always the main one (AC-297): a plugin opens this from its own window — an issue
        // dialog's "New session" button — and owned by the main window it would open behind the very window that
        // asked for it. Same reasoning as PluginDialogHost's owner pick.
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return null;
        }

        // One at a time (AC-367): you can only start one session, and a second copy would let two half-filled
        // forms compete over the same folder. A prefill that arrives while one is open is dropped along with the
        // duplicate — the operator is looking at a form they are already filling in, and overwriting it under
        // their hands is worse than ignoring a button they can press again.
        if (_surfaces.TryActivateAsync(typeof(NewSessionDialog)) is Task<NewSessionResult?> open)
        {
            return await open;
        }

        // The New-session picker reads the catalog (registry + plugin-provided servers, AC-11) so a plugin's
        // own MCP servers are offered and per-session uncheckable; the MCP-servers manager stays on the store.
        var viewModel = new NewSessionDialogViewModel(
            _profileStore, _loginChecker, _mcpServerCatalog, _workingPathStore, _conversationPickers,
            _ttyProviderResolver, _ttyProviderRegistry, _pluginProviderRegistry, _worktreeManager, _tokenEstimator,
            _projectStore, _oauthCoordinator, _memorySources, _loginStarter);
        await viewModel.LoadAsync();

        // The project (AC-164) before the prefill, and by identity out of the loaded list rather than the caller's
        // instance: selecting it runs the dialog's own project handling (folder, profile, worktree, MCP overlay), and
        // a prefill naming a field explicitly is the more specific answer, so it is applied over the result.
        if (project is not null)
        {
            viewModel.SelectedProject = viewModel.Projects.FirstOrDefault(candidate => candidate.Id == project.Id);

            // Selecting a project rebuilds the checklist from its overlay, and that is a read. Await it before the
            // dialog is on screen: a dialog that can be started while the rebuild is in flight can start a session
            // on the servers of no project at all.
            await viewModel.McpChecklistRefresh;
        }

        // Prefill (#AC-96): seed the dialog's fields *after* LoadAsync — the profile lookup needs the loaded list,
        // and setting properties earlier would be overwritten by the load's own defaulting. Every field is optional
        // and the operator can still change all of them; a profile label that matches nothing leaves the default pick.
        if (prefill is not null)
        {
            if (!string.IsNullOrWhiteSpace(prefill.ProfileLabel)
                && viewModel.Profiles.FirstOrDefault(profile =>
                    string.Equals(profile.Label, prefill.ProfileLabel, StringComparison.OrdinalIgnoreCase)) is { } matched)
            {
                viewModel.SelectedProfile = matched;
            }

            if (!string.IsNullOrWhiteSpace(prefill.WorkingDirectory))
            {
                viewModel.WorkingDirectory = prefill.WorkingDirectory;
            }

            if (!string.IsNullOrWhiteSpace(prefill.SessionName))
            {
                viewModel.SessionName = prefill.SessionName;
            }

            // The prompt is seeded to be *read*, not to be started with: nothing carries it out of the dialog (the
            // caller injects its own prefill into the started session). Without it the dialog asked the operator to
            // confirm a session while hiding the one field that can hold text written outside the cockpit.
            if (!string.IsNullOrWhiteSpace(prefill.InitialPrompt))
            {
                viewModel.InitialPrompt = prefill.InitialPrompt;
            }

            // Only a Claude profile keeps a resumable history; the dialog hides the resume controls for every other
            // provider (ShowResumeOptions). Gate the prefill the same way — otherwise a plugin could push the dialog
            // into resume-by-id on a provider that ignores it, or silently start a resume the operator never saw the
            // controls for. Resolved against whichever profile is selected now (the prefilled one, or the default).
            if (!string.IsNullOrWhiteSpace(prefill.ResumeSessionId) && viewModel.IsClaudeProfile)
            {
                viewModel.ResumeSessionId = prefill.ResumeSessionId;
                viewModel.ResumeMode = SessionResumeMode.BySessionId;
            }
        }

        // Reattach (AC-85): turn isolation on for the pre-filled folder, so starting the session re-owns that
        // existing worktree rather than picking a fresh folder. Only meaningful with a folder to isolate.
        if (isolateInWorktree && !string.IsNullOrWhiteSpace(viewModel.WorkingDirectory))
        {
            viewModel.IsolateInWorktree = true;
        }

        // The answer, read once the window has closed: a surface is not shown with ShowDialog, and the value
        // handed to Close() is only readable from that. Closing the window any other way — the X, Escape — never
        // raises the event, which is the cancel the caller expects.
        //
        // ⚠️ Subscribed BEFORE the DataContext is set, and that order is the whole thing. The dialog's own
        // code-behind subscribes from OnDataContextChanged, and its handler calls Close() — which raises Closed
        // synchronously, which is where the answer is read. Subscribed second, this line runs after the answer
        // has already been read as null: every Start session would return "cancelled" and start nothing.
        NewSessionResult? chosen = null;
        viewModel.CloseRequested += result => chosen = result;

        var dialog = new NewSessionDialog { DataContext = viewModel };

        // Managing profiles from within the New-session dialog opens the Manage dialog over it, then
        // reloads the picker so any added/edited/removed profile (and its defaults) shows immediately.
        // async void via the Action event: guard it so a dialog/store failure can't tear the process
        // down — worst case the picker just doesn't refresh.
        viewModel.ManageProfilesRequested += async () =>
        {
            try
            {
                await _ShowManageProfilesOverAsync(dialog);
                await viewModel.LoadAsync();
            }
            catch
            {
                // Managing profiles is best-effort from here; a failure must not crash the app.
            }
        };

        // Clone from a Git URL (AC-90): open the clone dialog over the New-session dialog, and on success drop the
        // local clone path into the folder field, from where isolation (AC-85) and the session start pick it up. The
        // clone dialog owns the failure path, so nothing is set here unless a real directory came back.
        // async void via the event: guard it so a clone/dialog failure can't tear the process down.
        viewModel.CloneFromUrlRequested += async () =>
        {
            try
            {
                if (await ShowCloneFromGitUrlAsync(dialog) is { Length: > 0 } clonePath)
                {
                    viewModel.WorkingDirectory = clonePath;
                }
            }
            catch
            {
                // Cloning is best-effort from here; a failure must not crash the app.
            }
        };

        return await _surfaces.ShowAsync(typeof(NewSessionDialog), dialog, owner, () => chosen);
    }

    public async Task ShowManageProfilesDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            await _ShowSurfaceAsync(typeof(ManageProfilesDialog), owner, _BuildManageProfilesAsync);
        }
    }

    public async Task ShowAssistantProfileDialogAsync(IAssistantSessionHost? assistant)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        // A surface, like Manage profiles: editing a provider's settings and picking servers is minutes of work,
        // and every running session — the assistant included — has to stay reachable behind it (AC-367).
        await _ShowSurfaceAsync(typeof(AssistantProfileDialog), owner, async () =>
        {
            var viewModel = new AssistantProfileDialogViewModel(
                _assistantProfileStore, _profileStore, assistant, _loginChecker,
                _pluginProviderRegistry, _mcpServerCatalog, _tokenEstimator, _ttyProviderResolver);
            await viewModel.LoadAsync();

            return new AssistantProfileDialog { DataContext = viewModel };
        });
    }

    public async Task ShowProjectsDialogAsync(ProjectsViewModel projects)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        // The one shared manager, so what this window shows is what the sidebar and the overview show.
        await _ShowSurfaceAsync(typeof(ProjectsDialog), owner, async () =>
        {
            await projects.LoadAsync();
            return new ProjectsDialog { DataContext = projects };
        });
    }

    public async Task<Project?> ShowProjectDialogAsync(Project? project, ISharedProjectSource? sharedSource = null)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        // Keyed on the project, not just the window type (AC-367): editing two different projects side by side is
        // fine, editing the same one twice is two forms saving over each other. A new project keys on null, so a
        // second "New project" focuses the empty form already open.
        var key = (typeof(ProjectDialog), project?.Id);
        if (_surfaces.TryActivateAsync(key) is Task<Project?> open)
        {
            return await open;
        }

        // AC-618: the categories already in use, so the editor can offer them as chips instead of the operator
        // retyping one. Read fresh rather than cached — this dialog opens far less often than the store changes.
        var knownCategories = (await _projectStore.LoadAsync()).CategoryOrder;

        // AC-247: a fresh read, not the claim-time snapshot _ClaimBoundProjects made — that snapshot can be hours
        // old by the time the operator presses Save, and its checksum has to be the one that read actually saw for
        // WriteBackAsync's own optimistic-concurrency check to mean anything. Read failing (offline, no longer a
        // member) leaves sharedWriteBack null below: the editor still opens, its claimed fields render locked (no
        // ProjectFieldOwnership override to unlock them without a checksum to write against), same as a project
        // whose source never claimed it editable at all.
        ProjectSharedWriteBackContext? sharedWriteBack = null;
        if (project is not null && sharedSource is not null)
        {
            var boundTo = project.Resources.FirstOrDefault(resource => resource.Role == ProjectResourceRole.Memory)?.Reference;
            if (boundTo is { Length: > 0 })
            {
                var bindingResult = await sharedSource.PrepareBindingAsync(boundTo, CancellationToken.None).ConfigureAwait(true);
                if (bindingResult is { Succeeded: true, Binding.Checksum.Length: > 0 } && bindingResult.Binding is { } baseline)
                {
                    sharedWriteBack = new ProjectSharedWriteBackContext(sharedSource, boundTo, baseline);
                }
            }
        }

        var viewModel = await ProjectDialogViewModel.CreateAsync(
            project, _profileStore, _mcpServerCatalog, _projectFields.Fields, _memorySources.Sources, _memorySources.Families,
            // AC-523: the "Servers…" flow re-reads the live registry through this rather than replaying the
            // snapshot above, so a connection added or removed in the settings screen it opens shows up back here.
            refreshMemorySources: () => (_memorySources.Sources, _memorySources.Families),
            // AC-604: a new project has no id yet, so nothing could have claimed it — only an existing project is
            // resolved against the ownership registry.
            fieldOwnership: project is not null ? _projectOwnership.Resolve(project.Id) : null,
            knownCategories: knownCategories,
            sharedWriteBack: sharedWriteBack);

        // Read once the window has closed; Close()'s value is only available from ShowDialog. Cancel and the
        // window's own X both leave this null, which is the same answer.
        //
        // ⚠️ Subscribed BEFORE the DataContext is set — see ShowNewSessionDialogAsync for why. Second in line
        // this runs after the answer has been read, and every Save would come back as a cancel.
        Project? saved = null;
        viewModel.CloseRequested += result => saved = result;

        var dialog = new ProjectDialog { DataContext = viewModel };

        // Cloning is answered here rather than in the dialog's code-behind: the clone flow owns a dialog of its
        // own and the manager that runs it, both of which live on this service.
        viewModel.CloneRequested += () => _ = _CloneIntoProjectAsync(viewModel, dialog);

        // AC-247: same split as cloning — SaveAsync raises the request, this service owns showing the window (a
        // Func so SaveAsync can await the operator's answer and act on it, rather than firing an event and hoping
        // something eventually calls back). `sharedWriteBack` is the same context CreateAsync above already
        // received, so its own Baseline is exactly what the conflict view needs to tell "the operator touched
        // this field" apart from "this only differs because Depot moved" — never re-derived, never re-read.
        if (sharedWriteBack is { } writeBack)
        {
            viewModel.ConflictRequested += (edit, latest) => _ResolveConflictAsync(edit, writeBack.Baseline, latest, dialog);
        }

        return await _surfaces.ShowAsync(key, dialog, owner, () => saved);
    }

    // Shows the conflict window over `owner` (the project editor itself, not the main window — AC-247 mirrors
    // _CloneIntoProjectAsync's own nested-dialog shape) and returns the operator's resolution, or null when they
    // cancelled it.
    private static async Task<ProjectDefinitionConflictResolution?> _ResolveConflictAsync(
        SharedProjectDefinitionEdit edit, SharedProjectBinding baseline, SharedProjectBinding latest, Window owner)
    {
        var viewModel = new ProjectDefinitionConflictViewModel(edit, baseline, latest);
        var dialog = new ProjectDefinitionConflictDialog { DataContext = viewModel };
        return await dialog.ShowDialog<ProjectDefinitionConflictResolution?>(owner);
    }

    public async Task<Project?> ShowSharedProjectBindingDialogAsync(SharedProject sharedProject, string sourceName, ISharedProjectSource source)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var key = (typeof(SharedProjectBindingDialog), sharedProject.Id);
        if (_surfaces.TryActivateAsync(key) is Task<Project?> open)
        {
            return await open;
        }

        var (viewModel, error) = await SharedProjectBindingDialogViewModel.CreateAsync(sharedProject.Id, sourceName, source, _profileStore);
        if (viewModel is null)
        {
            // The definition read failed (unreachable, not signed in, the project vanished between the list and
            // this click) — surfaced through the confirmation dialog's own single-button shape rather than opening
            // an unusable binding dialog. There is no plain one-button "OK" message dialog in this codebase yet;
            // "OK" in place of "Remove" is this call's whole adaptation, its return value unread.
            await ShowConfirmationDialogAsync("Couldn't finish setting up", error ?? "Could not read this project's definition.", confirmLabel: "OK");
            return null;
        }

        Project? saved = null;
        viewModel.CloseRequested += result => saved = result;

        var dialog = new SharedProjectBindingDialog { DataContext = viewModel };
        viewModel.CloneRequested += () => _ = _CloneIntoSharedProjectBindingAsync(viewModel, dialog);

        return await _surfaces.ShowAsync(key, dialog, owner, () => saved);
    }

    public async Task<Project?> ShowShareProjectDialogAsync(Project project, IReadOnlyList<ISharedProjectSource> publishSources)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var key = (typeof(ShareProjectDialog), project.Id);
        if (_surfaces.TryActivateAsync(key) is Task<Project?> open)
        {
            return await open;
        }

        var viewModel = ShareProjectDialogViewModel.Create(project, publishSources);
        Project? bound = null;
        viewModel.CloseRequested += result => bound = result;

        var dialog = new ShareProjectDialog { DataContext = viewModel };

        return await _surfaces.ShowAsync(key, dialog, owner, () => bound);
    }

    // Mirrors _CloneIntoProjectAsync, pre-filled with the shared definition's own GitUrl (AC-246: "Clone…" is an
    // offer built on a URL the operator never has to type in, not a general clone-from-anywhere flow).
    private async Task _CloneIntoSharedProjectBindingAsync(SharedProjectBindingDialogViewModel viewModel, Window owner)
    {
        var clonesRoot = await _cloneManager.GetEffectiveClonesRootAsync();
        var cloneViewModel = new CloneFromGitUrlDialogViewModel(_cloneManager, clonesRoot) { Url = viewModel.GitUrl ?? string.Empty };
        var dialog = new CloneFromGitUrlDialog { DataContext = cloneViewModel };

        if (await dialog.ShowDialog<string?>(owner) is { Length: > 0 } clonePath)
        {
            viewModel.ApplyPickedDirectory(clonePath, cloneViewModel.Url.Trim());
        }
    }

    // Keeps the URL beside the path: a project shows where its folder came from, which the clone dialog's own
    // result (a local path) cannot say on its own.
    private async Task _CloneIntoProjectAsync(ProjectDialogViewModel viewModel, Window owner)
    {
        var clonesRoot = await _cloneManager.GetEffectiveClonesRootAsync();
        var cloneViewModel = new CloneFromGitUrlDialogViewModel(_cloneManager, clonesRoot);
        var dialog = new CloneFromGitUrlDialog { DataContext = cloneViewModel };

        if (await dialog.ShowDialog<string?>(owner) is { Length: > 0 } clonePath)
        {
            viewModel.ApplyPickedDirectory(clonePath, cloneViewModel.Url.Trim());
        }
    }

    // Shows the clone-from-URL dialog over the New-session dialog and returns the local clone path, or null if the
    // operator cancelled. The dialog runs the clone itself (through the injected manager) and surfaces its own
    // failures, so this only ever hands back a directory that is actually on disk.
    private async Task<string?> ShowCloneFromGitUrlAsync(Window owner)
    {
        // Resolve the clones root once, here, so the dialog's target preview and its "Default:" hint reflect the
        // operator's override (AC-90) without the view model re-reading the setting on every keystroke.
        var clonesRoot = await _cloneManager.GetEffectiveClonesRootAsync();
        var viewModel = new CloneFromGitUrlDialogViewModel(_cloneManager, clonesRoot);
        var dialog = new CloneFromGitUrlDialog { DataContext = viewModel };
        return await dialog.ShowDialog<string?>(owner);
    }

    // Modal over the form it was opened from, unlike the Manage-profiles the cockpit opens itself (AC-367). It is
    // not the cockpit this holds — that form is already a surface, so sessions stay reachable behind it — it is
    // the form underneath, which owns it. As a surface it could be left open while its owner closes, and Avalonia
    // takes an owned window down with its owner: a profile being edited would vanish without being asked.
    private async Task _ShowManageProfilesOverAsync(Window owner) =>
        await (await _BuildManageProfilesAsync()).ShowDialog(owner);

    private async Task<Window> _BuildManageProfilesAsync()
    {
        var viewModel = new ManageProfilesDialogViewModel(_profileStore, _loginChecker, _modelCatalog, _pluginProviderRegistry, _mcpServerCatalog, _tokenEstimator, _ttyProviderResolver, _loginStarter);
        await viewModel.LoadAsync();

        return new ManageProfilesDialog { DataContext = viewModel };
    }

    public async Task ShowMcpServersDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(McpServersDialog), owner, async () =>
        {
            var viewModel = new McpServersViewModel(_mcpServerStore, _internalMcpProviders, _oauthCoordinator);
            await viewModel.LoadAsync();

            var dialog = new McpServersDialog { DataContext = viewModel };
            viewModel.CloseRequested += dialog.Close;

            return dialog;
        });
    }

    public async Task ShowVerifyRunnersDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(VerifyRunnersDialog), owner, async () =>
        {
            var viewModel = new VerifyRunnersViewModel(_verifyRunnerRegistry);
            await viewModel.LoadAsync();

            return new VerifyRunnersDialog { DataContext = viewModel };
        });
    }

    public async Task ShowPluginStoreDialogAsync(PluginManagerViewModel manager, PluginStoreFilter? initialFilter = null)
    {
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(PluginStoreDialog), owner, async () =>
        {
            var viewModel = new PluginStoreDialogViewModel(manager, initialFilter);
            var dialog = new PluginStoreDialog { DataContext = viewModel };
            await viewModel.LoadAsync();

            return dialog;
        });
    }

    // Most dialogs are only ever opened from the main window, so they hardcode it as the owner. The store
    // dialog can itself be a step below another window (Options → Store), so it — and anything the store
    // opens in turn, like plugin consent — needs the topmost active window instead, or it centres behind
    // the window it was opened from rather than over it (#62 design-doc caveat).
    private static Window? _ActiveOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } lifetime)
        {
            return null;
        }

        return lifetime.Windows.LastOrDefault(window => window.IsActive) ?? main;
    }

    // Opens a surface that answers nothing, and waits for it to close like the modal it used to be (AC-367).
    // The window is built by a callback rather than passed in because nearly every surface reads a store to
    // populate itself: asked for one that is already open, that work would be done for a window thrown away.
    private async Task _ShowSurfaceAsync(object key, Window owner, Func<Task<Window>> createSurface)
    {
        if (_surfaces.TryActivateAsync(key) is { } open)
        {
            await open;
            return;
        }

        await _surfaces.ShowAsync(key, await createSurface(), owner);
    }

    // The same, for a surface that needs nothing read before it can be built.
    private Task _ShowSurfaceAsync(object key, Window owner, Func<Window> createSurface) =>
        _ShowSurfaceAsync(key, owner, () => Task.FromResult(createSurface()));

    public async Task<(DateTimeOffset Moment, string Prompt)?> ShowScheduleResumeDialogAsync(DateTimeOffset suggested, string prompt)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var viewModel = new ScheduleResumeDialogViewModel(suggested, prompt);
        var dialog = new ScheduleResumeDialog { DataContext = viewModel };

        return await dialog.ShowDialog<ScheduleResumeDialogViewModel?>(owner) is { } chosen
            ? (chosen.Moment, chosen.Prompt)
            : null;
    }

    public async Task ShowOptionsDialogAsync(CockpitViewModel viewModel)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(OptionsDialog), owner, () => new OptionsDialog { DataContext = viewModel });

        // AC-233: the usage thresholds are edited in place like every other option here, so they are written when
        // the dialog closes. Sessions already open keep the numbers they were started with; the ones started after
        // this take the new ones.
        if (viewModel.UsageThresholdSettings is { } thresholds)
        {
            await thresholds.SaveAsync();
            viewModel.UsageThresholds = await thresholds.ReloadAsync();
        }
    }

    // A dashboard travels as ordinary JSON with its own extension: readable enough to look at before you trust
    // one someone sent you, and distinct enough that the picker does not offer every .json on the machine.
    private static FilePickerFileType DashboardFile { get; } =
        new("Cockpit dashboard") { Patterns = ["*.cockpit-dashboard.json", "*.json"] };

    public async Task<string?> PickDashboardToImportAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import dashboard",
            AllowMultiple = false,
            FileTypeFilter = [DashboardFile],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickDashboardExportPathAsync(string suggestedName)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export dashboard",
            SuggestedFileName = $"{suggestedName}.cockpit-dashboard.json",
            DefaultExtension = "json",
            FileTypeChoices = [DashboardFile],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickPluginZipAsync()
    {
        // The active window, not always MainWindow (AC-456), matching the folder picker below. Owned by the main
        // window this picker left the store dialog it was launched from clickable, so its install buttons stayed
        // live while the operator browsed. The store is a surface now rather than a modal, so it no longer holds
        // the cockpit either way — but the picker still belongs to the window it was opened from.
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return null;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install plugin from zip",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Plugin package (*.zip)") { Patterns = ["*.zip"] }],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickPluginStoreFolderAsync()
    {
        // The active window, not always MainWindow: the picker is launched from the Manage-stores dialog, itself
        // an owned modal over the store surface, so it must attach to that stack rather than behind it.
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return null;
        }

        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a plugin store folder",
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ShowPluginConsentAsync(PluginConsentInfo info)
    {
        // Uses the active window, not always MainWindow: an install/update triggered from the plugin store
        // dialog (itself opened over Options) must show consent over that dialog stack, not behind it.
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return false;
        }

        var dialog = new PluginConsentDialog { DataContext = info };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task ShowDelegatedTasksDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        // The shared view model, so the dialog lists the same tasks the orchestrator tools act on.
        await _ShowSurfaceAsync(typeof(DelegatedTasksDialog), owner, () => new DelegatedTasksDialog { DataContext = _delegatedTasks });
    }

    public async Task ShowAgentLineInspectorDialogAsync(AgentLineInspectorViewModel inspector)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        // Read once on opening, so the window is never blank on the way in; the Refresh button is how it is brought
        // up to date after that. Not a live subscription: this is a read-only look at an append-only trail, and a
        // window that redrew itself under the operator's eyes while they were reading a line is harder to read, not
        // easier.
        await inspector.RefreshAsync();
        await _ShowSurfaceAsync(typeof(AgentLineInspectorDialog), owner, () => new AgentLineInspectorDialog { DataContext = inspector });
    }

    public async Task ShowWorktreesDialogAsync(WorktreesViewModel worktrees)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        // The caller's shared view model, so the dialog and the status-bar counter read the same worktrees. Refreshed
        // to the real git state (clean/dirty, owner live/gone) before it opens.
        await _ShowSurfaceAsync(typeof(WorktreesDialog), owner, async () =>
        {
            await worktrees.RefreshAsync();
            return new WorktreesDialog { DataContext = worktrees };
        });
    }

    public async Task ShowAboutDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(AboutDialog), owner, () =>
        {
            var pluginProviders = _pluginProviderRegistry.Registrations.Select(registration => registration.DisplayName);
            var info = AboutInfo.FromAssembly(Assembly.GetExecutingAssembly(), pluginProviders);

            return new AboutDialog { DataContext = info };
        });
    }

    public async Task ShowGlossaryDialogAsync()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        await _ShowSurfaceAsync(typeof(GlossaryDialog), owner, () => new GlossaryDialog());
    }

    public async Task ShowCommandPaletteDialogAsync(IReadOnlyList<PaletteCommand> commands)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        var viewModel = new CommandPaletteDialogViewModel(commands);
        var dialog = new CommandPaletteDialog { DataContext = viewModel };
        await dialog.ShowDialog(owner);

        // Run the chosen command after the palette has closed, so a command that opens another dialog isn't
        // stacked underneath it.
        viewModel.Chosen?.Invoke();
    }

    public async Task<bool> ShowConfirmationDialogAsync(string title, string message, string confirmLabel = "Remove")
    {
        // Owner is the topmost window so the confirm sits over whatever dialog triggered it (e.g. the store).
        if (_ActiveOwnerWindow() is not { } owner)
        {
            return false;
        }

        var dialog = new ConfirmationDialog { DataContext = new ConfirmationDialogViewModel(title, message, confirmLabel) };
        return await dialog.ShowDialog<bool>(owner);
    }

    public async Task<string?> ShowSetStatusDialogAsync(string currentStatusline)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return null;
        }

        var dialog = new SetStatusDialog { DataContext = new SetStatusDialogViewModel(currentStatusline) };
        return await dialog.ShowDialog<string?>(owner);
    }
}

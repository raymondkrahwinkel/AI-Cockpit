using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.App.Logging;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.ViewModels.Onboarding;
using Cockpit.App.Views;
using Cockpit.App.Views.Onboarding;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.StatusBar;
using Cockpit.Plugins.Abstractions.Workflows;

using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;
namespace Cockpit.App;

public partial class App : Application
{
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private MainWindow? _mainWindow;
    private UnlockWindow? _screenLockWindow;
    private DispatcherTimer? _pluginUpdateTimer;

    // The teardown this app is already running, so a second shutdown request rides along with the first (AC-958).
    private Task? _teardown;

    // True once a real quit was requested (tray "Quit"), so `MainWindow` lets the close
    // through instead of hiding to tray (#33). Distinguishes a genuine quit from a close-to-tray.
    public bool IsQuitting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // macOS builds its Apple-menu/About/Hide/Quit labels from Application.Name, not the bundle's Info.plist.
        Name = CockpitProduct.DisplayName;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

            // The desktop's own route into a shutdown — an OS log-off or restart ending the session — as something
            // distinct from the tray's Quit. Both used to end the process through the same silent teardown, so an
            // operator finding the cockpit gone had no way to tell "Windows ended my session" from anything else.
            desktop.ShutdownRequested += (_, shutdown) =>
            {
                LifecycleLog.Write($"The desktop lifetime requested shutdown (quit already requested: {IsQuitting}).");

                // AC-958: hold every request — each carries its own args, so cancelling the first says nothing about
                // a second one arriving mid-teardown — and shut down once the sessions are actually torn down.
                shutdown.Cancel = true;
                _ = TearDownThenShutdownAsync();
            };

            // Encrypted credentials: the key comes from a password, so the cockpit cannot be built yet — the view
            // model, the plugins and the MCP servers all read settings, and reading them without the key would
            // hand them ciphertext. The unlock window goes first and the app starts behind it.
            var protection = Program.Services.GetRequiredService<ISecretProtectionService>();
            if (_IsLockedAtStartup(protection))
            {
                _ShowUnlockWindow(desktop, protection);
                base.OnFrameworkInitializationCompleted();

                return;
            }

            _StartCockpitAndOnboard(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    // AC-509 criterion 4: unlock first, onboarding after. Every route that ends in the main window being shown
    // goes through this — see the Show()-inventory in the App.axaml.cs class doc and the two callers below.
    private void _StartCockpitAndOnboard(IClassicDesktopStyleApplicationLifetime desktop)
    {
        // Read before either window exists, the same way _IsLockedAtStartup reads its own decision: this picks
        // which window shows next, so it has to be known before that window is built rather than raced against it.
        if (_NeedsOnboarding())
        {
            _ShowOnboardingWizard(desktop);

            return;
        }

        _StartCockpit(desktop);
    }

    // A broken read must not stop the app from starting at all — same reasoning as _IsLockedAtStartup, and the
    // same fail-open: skipping onboarding once is recoverable (Help menu's "Run setup again", AC-512), a cockpit
    // that never starts is not.
    private static bool _NeedsOnboarding()
    {
        try
        {
            return Program.Services.GetRequiredService<IFirstRunWizardStateStore>()
                .GetCompletedVersionAsync().GetAwaiter().GetResult() is null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // Keep the wizard open until the cockpit is shown so desktop lifetime never sees zero windows. Building it
    // here avoids IFirstRunWizard's close-before-return contract (AC-512); Closing covers every exit route.
    private void _ShowOnboardingWizard(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var stateStore = Program.Services.GetRequiredService<IFirstRunWizardStateStore>();
        var viewModel = new FirstRunWizardViewModel(
            [.. Program.Services.GetServices<IFirstRunWizardStep>()],
            FirstRunWizardViewModel.EpicPlan);
        var window = new FirstRunWizardWindow { DataContext = viewModel };

        window.Closing += (_, _) =>
        {
            _ = stateStore.MarkCompletedAsync(FirstRunWizardVersion.Current);
            _StartCockpit(desktop);
        };

        desktop.MainWindow = window;

        // Avalonia auto-shows only the first MainWindow; after startup unlock, this replacement needs Show().
        window.Show();
    }

    // Use the settings-store retry for concurrent saves (review #9). An unreadable file falls through to normal
    // startup so store recovery can report it in a window, rather than opening an unlock screen backed by that read.
    private static bool _IsLockedAtStartup(ISecretProtectionService protection)
    {
        try
        {
            return protection.GetStatusAsync().GetAwaiter().GetResult() is { Enabled: true, Unlocked: false };
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The unlock window is the app's only window until the password is right. It is the lifetime's MainWindow so
    // the framework shows it; the real one replaces it, and is shown before this one closes — a moment with no
    // window at all is a moment the desktop lifetime reads as "the app is done".
    private void _ShowUnlockWindow(IClassicDesktopStyleApplicationLifetime desktop, ISecretProtectionService protection)
    {
        var viewModel = new UnlockViewModel(protection);
        var window = new UnlockWindow { DataContext = viewModel };

        viewModel.Unlocked += (_, _) =>
        {
            // AC-509 criterion 4: unlock first, onboarding after — this is the "locked at startup" half of that
            // ordering, the other half is the direct call in OnFrameworkInitializationCompleted below.
            _StartCockpitAndOnboard(desktop);
            window.Close();
        };

        desktop.MainWindow = window;
    }

    // Locks only the running cockpit UI; the in-memory key and agents remain active (AC-5). The coordinator provides
    // UI-thread dispatch and idempotence, while completion signals that a later OS lock may lock again.
    private async Task _LockToUnlockScreen()
    {
        if (_mainWindow is null)
        {
            return;
        }

        // This route is reachable only after onboarding, so it needs no separate gate (AC-509). Bring a hidden or
        // minimized cockpit forward first so the modal has a visible owner and does not look like a freeze.
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;

        var protection = Program.Services.GetRequiredService<ISecretProtectionService>();
        var viewModel = new UnlockViewModel(protection);
        var window = new UnlockWindow { DataContext = viewModel, Topmost = true };

        // Same contract as startup: the password is the key, and Unlocked fires once it is right (or once the operator
        // took the forgotten-password way out, which turns encryption off — after which nothing re-locks). Closing the
        // dialog is what completes ShowDialog's task.
        viewModel.Unlocked += (_, _) => window.Close();

        // Held for as long as the screen is up so the OS unlock can hand it the keyboard (AC-187) — it was shown while
        // the desktop was still locked, where activation does not stick.
        _screenLockWindow = window;

        // Modality does not cover sibling work surfaces (AC-367), so hide and later restore them to lock the whole
        // app without discarding their state.
        using var surfaces = Program.Services.GetRequiredService<SurfaceWindows>().HideAll();

        try
        {
            await window.ShowDialog(_mainWindow);
        }
        finally
        {
            _screenLockWindow = null;
        }
    }

    private void _StartCockpit(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var cockpitViewModel = Program.Services.GetRequiredService<CockpitViewModel>();

        // AC-1094: core (non-plugin) sources of supervised background activity, e.g. RunTracker for a tracked test
        // run — resolved by the public interface only, so this needs no reference to any concrete Infrastructure
        // type. Plugins add theirs later, through ICockpitHost, as they load.
        foreach (var source in Program.Services.GetServices<ISupervisedActivitySource>())
        {
            cockpitViewModel.PluginSupervisedActivities.Add(source);
        }

        // ProfileDisplayConverter is x:Static rather than DI-created, so supply the now-populated registry here to
        // show plugin provider names instead of "(Plugin)".
        Converters.ProfileDisplayConverter.PluginProviderRegistry =
            Program.Services.GetRequiredService<Cockpit.Infrastructure.Sessions.IPluginProviderRegistry>();

        _mainWindow = new MainWindow
        {
            DataContext = cockpitViewModel,
        };
        desktop.MainWindow = _mainWindow;

        // Avalonia cannot re-show a closed window; clearing it here makes every show route use its existing null
        // guard instead of crashing from a tray handler.
        _mainWindow.Closed += (_, _) => _mainWindow = null;

        // The onboarding gate owns every creation route (AC-509). Show explicitly because Avalonia will not
        // auto-show a MainWindow that replaces the startup unlock window.
        _mainWindow.Show();
        _SetUpTrayIcon();

        // Render the default workspace immediately, then asynchronously load saved workspaces and their panes in
        // sequence because pane restore reads Workspaces.Settings (AC-410).
        _ = _RestoreCockpitAsync(cockpitViewModel);

        // The feature coordinators are resolved for their constructors: each subscribes to the hotkey
        // coordinator there, and a singleton nobody asks for is never built — so an unresolved one would
        // simply not be listening when its key fires.
        Program.Services.GetRequiredService<VoicePushToTalkCoordinator>();

        // Resolve the assistant hotkey to start listening, and refresh it after Options saves so changes apply
        // immediately (AC-543). This does not start an assistant instance.
        var assistantPushToTalk = Program.Services.GetRequiredService<AssistantPushToTalkCoordinator>();

        // Load assistant availability at startup so the first F10 does not use the stale off-by-default state.
        // Availability checks start no instance; the first hold or click still does that.
        var assistantHost = Program.Services.GetRequiredService<AssistantSessionHost>();
        _ = assistantHost.ApplySettingsAsync();

        // Handed over rather than injected: the host is built *from* the cockpit view model, so the view model
        // cannot take it as a constructor argument. Options → Voice needs it for the one thing only a living
        // assistant can do — restart onto a permission mode it was not launched with.
        cockpitViewModel.AssistantHost = assistantHost;

        // The chip, and what feeds it. Started here rather than in its constructor because it subscribes to the
        // open-mic coordinator, which is resolved further down — and because the view model it hands over has to
        // exist before the sidebar binds to it.
        var assistantIndicator = Program.Services.GetRequiredService<AssistantIndicatorCoordinator>();
        assistantIndicator.Start();
        assistantPushToTalk.FollowSettings(cockpitViewModel.AssistantOptions, assistantIndicator);

        // The broker reads the consent-bypass snapshot synchronously; refresh its singleton after Options saves so
        // disabled sources stop bypassing on the next request, not the next restart (AC-575).
        var consentBypass = Program.Services.GetRequiredService<AssistantConsentBypassPolicy>();
        cockpitViewModel.AssistantOptions.Saved += (_, _) => _ = consentBypass.ApplySettingsAsync();

        assistantIndicator.SetCollapsed(cockpitViewModel.SidebarCollapsed);
        cockpitViewModel.AssistantIndicator = assistantIndicator.Indicator;
        cockpitViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CockpitViewModel.SidebarCollapsed))
            {
                assistantIndicator.SetCollapsed(cockpitViewModel.SidebarCollapsed);
            }
        };

        // Held on the view model as well as resolved: every session panel is handed the capture its composer
        // button runs from here, the same way the open-mic coordinator is exposed for the sidebar toggle.
        cockpitViewModel.Screenshots = Program.Services.GetRequiredService<ScreenshotCoordinator>();

        // Fire-and-forget (#34, AC-220): arms every desktop-wide key the operator switched on, as one
        // registration. A no-op when none of them is, so the portal/keyboard-hook is only ever touched for an
        // operator who opted in.
        _ = Program.Services.GetRequiredService<GlobalHotkeyCoordinator>().ApplyAsync();

        // When enabled, an OS lock triggers the pure UI lock while the key and agents stay active (AC-5). The
        // coordinator owns gating/idempotence; App supplies and awaits the unlock window.
        var screenLock = Program.Services.GetRequiredService<ScreenLockCoordinator>();
        screenLock.LockAction = () => Dispatcher.UIThread.InvokeAsync(_LockToUnlockScreen);
        screenLock.RestoreFocusAction = () => Dispatcher.UIThread.Post(() => _screenLockWindow?.TakeFocus());
        _ = screenLock.StartAsync();

        // Open-mic dictation: expose the coordinator so the sidebar toggle can turn it on/off at
        // runtime, and resume listening at startup if it was left on. No-op when voice is off.
        var openMicCoordinator = Program.Services.GetRequiredService<OpenMicCoordinator>();
        cockpitViewModel.OpenMic = openMicCoordinator;
        _ = openMicCoordinator.StartAsync();

        // AC-718: started here, not as an IHostedService — Dispatcher.UIThread is only safe to touch once
        // Avalonia's own Setup() has bound it to this thread, which by this point in the lifecycle it has.
        Program.Services.GetRequiredService<DiagnosticsBackgroundService>().Start();

        // AC-733: a plain background thread, not UI-bound — started here too, just to sit beside its sibling.
        Program.Services.GetRequiredService<Services.AdaptiveGcCompactor>().Start();

        // Attach the scheduler outside the view-model constructor so test/design graphs never write to disk
        // (AC-234), and do so before plugins can create sessions that would otherwise miss pending resumes.
        cockpitViewModel.ScheduledResumes = Program.Services.GetService<ScheduledResumeCoordinator>();

        // #14 Plugins — phase 2: now the container and the cockpit view model exist, hand each loaded
        // plugin the host built for it so it can register its Options tab / side-menu section.
        _InitializePlugins();

        // Silent unless the operator is carrying a plugin this build has replaced, in which case they are told
        // and asked — rather than having it cleaned out of their plugins folder behind their back.
        _ = Program.Services.GetRequiredService<SupersededPluginNotice>().CheckAsync();

#if DEBUG
        // AC-185: the dev inner loop — watches plugins-dev for a rebuild and offers one toast action to reload
        // it, instead of a manual restart after every build. DEBUG only, and a no-op off a dev checkout.
        Program.Services.GetService<DevPluginReloadWatcher>()?.Start();
#endif

        // Check plugin updates after discovery and every 15 minutes (#59), plus the cockpit once at startup when
        // enabled (#71). Background failures stay silent and surface only when requested from Options.
        _ = cockpitViewModel.InitialiseUpdatesAsync();
        // AC-188: and keep looking every hour after that, so a window left open for a workday still learns about a
        // build cut hours later. Reuses the same toast/banner/dedup path; stopped when the view model disposes.
        cockpitViewModel.StartPeriodicUpdateChecks();

        // AC-234: and now start it watching the clock, once the sessions it resolves against can exist.
        _ = _StartScheduledResumesAsync(cockpitViewModel);

        // AC-634: watch the branches the sessions are on for a failing CI check. The watch set is the live sessions
        // rather than a configured list, so a worktree opened later is followed without anyone saying so.
        if (Program.Services.GetService<Services.CiWatcher>() is { } ciWatcher)
        {
            ciWatcher.Watching = () =>
            [
                .. cockpitViewModel.AllSessions()
                    .Where(session => !string.IsNullOrWhiteSpace(session.WorkingDirectory))
                    .Select(session => new Services.WatchedCheckout(session.PaneId, session.Title, session.WorkingDirectory!)),
            ];
            ciWatcher.Start();
        }

        // AC-640: the same shape one layer along, for the sessions the assistant armed a watch on with
        // `watch_session`. Started with nothing watched, unlike the CI one: it only ever follows what it was asked to.
        if (Program.Services.GetService<Services.SessionWatcher>() is { } sessionWatcher)
        {
            sessionWatcher.Probe = Services.SessionWatcher.ProbeOf(cockpitViewModel);
            sessionWatcher.Start();
        }

        // AC-656: and give every pane a turn as soon as its own inbox has mail, instead of leaving it for that
        // pane's next turn or tool call to notice. Unlike SessionWatcher this needs nothing armed — every live pane
        // is checked, the assistant included (`AllSessions` does not carry it; `cockpit-agents` reaches it anyway).
        if (Program.Services.GetService<Services.InboxWakeScheduler>() is { } inboxWakeScheduler)
        {
            inboxWakeScheduler.Panes = () =>
            [
                Cockpit.Core.Assistant.AssistantIdentity.PaneId,
                .. cockpitViewModel.AllSessions().Select(session => session.PaneId),
            ];
            inboxWakeScheduler.Start();
        }

        // AC-643: and keep the worktree crash net ticking after the startup sweep, against the sessions that are
        // live at that moment — a worktree whose owner crashed at noon is reconciled then, not at the next restart.
        if (Program.Services.GetService<Services.WorktreeReconciler>() is { } worktreeReconciler)
        {
            // AC-654: asked of the liveness registry rather than the grid, because a pane-only answer misses the
            // sessions that run without one (a delegated task, AC-106) and sweeps the worktree out from under them.
            var liveSessions = Program.Services.GetService<Cockpit.Core.Abstractions.Sessions.ILiveSessionRegistry>();
            worktreeReconciler.LiveSessionIds = liveSessions is { } registry
                ? () => registry.LiveSessionIds
                : () => cockpitViewModel.AllSessions().Select(session => session.PaneId).ToList();
            worktreeReconciler.Start();
        }

        // AC-894: poll every Depot-bound project's checksum for a change made elsewhere, and let "Sync now" force
        // the same check for one project outside the timer.
        if (Program.Services.GetService<Services.DepotSyncWatcher>() is { } depotSyncWatcher)
        {
            depotSyncWatcher.BoundProjects = () => cockpitViewModel.Projects.DepotBoundProjects();
            depotSyncWatcher.OnChecked = (projectId, changed, logoBytes) => cockpitViewModel.Projects.SetRemoteChangeState(projectId, changed, logoBytes);
            cockpitViewModel.Projects.SyncNow = project => depotSyncWatcher.SyncNowAsync(project.Id);
            depotSyncWatcher.Start();
        }

        // AC-644: the same crash net one layer up, for the claims a session that never closed left standing.
        if (Program.Services.GetService<Services.StaleClaimReaper>() is { } claimReaper)
        {
            claimReaper.LivePaneIds = () =>
            [
                // The assistant, which `AllSessions` does not carry, holds claims like anyone else: `cockpit-agents`
                // is AlwaysMounted and reaches it too. Left out, its own claims would be reaped on the first tick.
                Cockpit.Core.Assistant.AssistantIdentity.PaneId,
                .. cockpitViewModel.AllSessions().Select(session => session.PaneId),
            ];
            claimReaper.Start();
        }

        // AC-233: the operator's own thresholds, loaded once and handed to every session started after this, plus
        // the settings screen that edits them.
        if (Program.Services.GetService<IUsageThresholdStore>() is { } thresholdStore)
        {
            _ = _LoadUsageThresholdsAsync(cockpitViewModel, thresholdStore);
        }

        // AC-1001: Options → Profiles, built the same way SessionDialogService builds the standalone dialog it
        // replaces — same services, same view model type, just handed to the cockpit instead of a window.
        cockpitViewModel.Profiles = new ManageProfilesDialogViewModel(
            Program.Services.GetRequiredService<ISessionProfileStore>(),
            Program.Services.GetRequiredService<IProfileLoginChecker>(),
            Program.Services.GetService<IModelCatalog>(),
            Program.Services.GetService<IPluginProviderRegistry>(),
            Program.Services.GetService<IMcpServerCatalog>(),
            Program.Services.GetService<IMcpToolTokenEstimator>(),
            Program.Services.GetService<ITtySessionProviderResolver>(),
            Program.Services.GetService<IProfileLoginStarter>());

        // AC-1002: Options → MCP Servers, built the same way SessionDialogService builds the standalone dialog it
        // replaces — same services, same view model type, just handed to the cockpit instead of a window.
        cockpitViewModel.McpServers = new McpServersViewModel(
            Program.Services.GetRequiredService<IMcpServerStore>(),
            Program.Services.GetServices<ICockpitInternalMcpProvider>(),
            Program.Services.GetService<IMcpOAuthCoordinator>());

        var pluginUpdateChecker = Program.Services.GetRequiredService<IPluginUpdateChecker>();
        // The managed-CLI update check (#AC-20) rides the same timer: one look on startup, then every 15 minutes,
        // toasting once when an installed managed CLI (claude/codex) has a newer version available.
        var managedCliUpdateChecker = Program.Services.GetRequiredService<Services.ManagedCliUpdateChecker>();
        _ = pluginUpdateChecker.CheckNowAsync();
        _ = managedCliUpdateChecker.CheckNowAsync();
        _pluginUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _pluginUpdateTimer.Tick += (_, _) =>
        {
            _ = pluginUpdateChecker.CheckNowAsync();
            _ = managedCliUpdateChecker.CheckNowAsync();
        };
        _pluginUpdateTimer.Start();
    }

    // Phase 2 of the plugin lifecycle: each plugin gets a CockpitHost carrying the built service provider,
    // the cockpit as the contribution sink, the shared actions, and its own persisted storage slice.
    private void _InitializePlugins()
    {
        if (Program.Services.GetService<PluginManager>() is not { } pluginManager)
        {
            return;
        }

        var cockpit = Program.Services.GetRequiredService<CockpitViewModel>();
        var registrationStore = Program.Services.GetRequiredService<IPluginRegistrationStore>();
        var secretFieldStore = Program.Services.GetRequiredService<IPluginSecretFieldStore>();
        var dialogHost = Program.Services.GetRequiredService<IPluginDialogHost>();

        // Register plugin-declared secret key names before reading settings, or ciphertext could reach a plugin
        // and remain in a backup labelled credential-free. The names themselves require no key.
        var declared = secretFieldStore.LoadAsync().GetAwaiter().GetResult()
            .Concat(pluginManager.Loaded.SelectMany(discovered => discovered.Manifest.SecretKeys))
            .ToList();

        if (declared.Count > 0)
        {
            SecretKeyHolder.Shared.Declare(declared);

            // A plugin's declared field names can turn a value the host did not recognise as a credential into one
            // it does, so the awareness banner (AC-41) has to re-evaluate now that the field set is complete —
            // otherwise a plugin token in the clear would go unmentioned until the next save.
            _ = cockpit.Security.RefreshAsync();
        }

        // AC-34: seed the terminal-access master switch from its persisted setting before any session can start, so a
        // session that launches before the operator ever opens Options still reflects the saved choice (default off).
        Program.Services.GetRequiredService<ITerminalAccessSwitch>().Enabled =
            Program.Services.GetRequiredService<ITerminalAccessSettingsStore>().LoadAsync().GetAwaiter().GetResult().Enabled;

        // AC-1066: same seeding, for the shell-access master switch.
        Program.Services.GetRequiredService<IShellAccessSwitch>().Enabled =
            Program.Services.GetRequiredService<IShellAccessSettingsStore>().LoadAsync().GetAwaiter().GetResult().Enabled;

        var actions = new PluginActions(
            cockpit,
            () => _mainWindow is null ? null : TopLevel.GetTopLevel(_mainWindow)?.Clipboard,
            Program.Services.GetRequiredService<ISessionDialogService>(),
            Program.Services.GetRequiredService<ISessionProfileStore>(),
            Program.Services.GetRequiredService<IDelegationService>());

        // One shared read/observe surface across all plugins, mirroring the single shared actions surface.
        var sessionObserver = new PluginSessionObserver(cockpit);

        // The same singleton the startup banner (CockpitViewModel) and the Plugin manager read (#184) — a
        // contribution failure recorded here reaches both without a second source of truth to keep in sync.
        var diagnostics = Program.Services.GetRequiredService<PluginDiagnostics>();

        pluginManager.Initialize((discovered, plugin) => new CockpitHost(
            discovered.FolderId,
            discovered.Manifest.Name,
            Program.Services,
            cockpit,
            actions,
            _CreatePluginStorage(discovered, registrationStore, secretFieldStore),
            dialogHost,
            sessionObserver,
            diagnostics,
            // The keys this plugin says hold a credential. They already gate encryption and the backup scrubber;
            // handing them to the host lets a dashboard export drop them too, which is the third place a
            // declared secret has to be honoured.
            discovered.Manifest.SecretKeys,
            // AC-499: this plugin's own runtime type, so the host can tell its own IPluginMcpProvider registration
            // apart from every other plugin's when it resolves a tool call's caller-scoped fallback — see
            // CockpitHost's own parameter doc.
            plugin.GetType()));

        // The templates installed from a store (#69) join the ones the plugins ship, in the same registry: to the
        // operator "a flow somebody already drew" is one kind of thing, whether it came with a plugin or from a store.
        // Read after the plugins have registered theirs, so an id clash is the store's copy losing to the plugin's own.
        _RegisterInstalledTemplates(
            Program.Services.GetRequiredService<IWorkflowTemplateLibrary>(),
            Program.Services.GetRequiredService<IWorkflowTemplateRegistry>());

        // After plugin phase 2 exposes connections, migrate legacy name-keyed OAuth tokens to stable IDs (AC-403).
        // Block the already-visible UI until completion so it cannot briefly report a present credential missing;
        // later launches perform only a small, non-writing config read.
        Program.Services.GetRequiredService<McpOAuthTokenAdoption>().RunAsync().GetAwaiter().GetResult();

        // Surface any load/init failures (phase 1 or 2), and any plugins now awaiting approval (AC-208), as
        // banners; the app kept running regardless.
        cockpit.RefreshPluginFailures();
    }

    private static void _RegisterInstalledTemplates(IWorkflowTemplateLibrary library, IWorkflowTemplateRegistry registry)
    {
        foreach (var installed in library.Load())
        {
            try
            {
                registry.Register(new WorkflowTemplate(
                    installed.Id,
                    installed.Name,
                    installed.Description ?? string.Empty,
                    installed.Json,
                    installed.Category ?? "Installed"));
            }
            catch (InvalidOperationException)
            {
                // A plugin already offers a template under this id — its own copy wins, and the store's is skipped
                // rather than taking the app down over a name.
            }
        }
    }

    // Seeds the plugin's storage from its saved slice and writes changes back through the store; the load
    // blocks briefly on the small config file at startup, which is acceptable on the UI thread here.
    private static PluginStorage _CreatePluginStorage(
        DiscoveredPlugin discovered,
        IPluginRegistrationStore store,
        IPluginSecretFieldStore secretFieldStore)
    {
        var seed = store.LoadDataAsync(discovered.FolderId).GetAwaiter().GetResult();

        return new PluginStorage(
            seed,
            data => _ = store.SaveDataAsync(discovered.FolderId, data),
            // A key a plugin calls SetSecret on is remembered for the next start too: the name is what tells the
            // host to decrypt that field on the way in, and it would otherwise only be known while the plugin that
            // wrote it happened to be running.
            key =>
            {
                SecretKeyHolder.Shared.Declare([key]);
                _ = secretFieldStore.DeclareAsync(discovered.FolderId, [key]);
            });
    }

    // Restores and focuses the main window from the tray. A non-null window proves onboarding already ran, so this
    // route needs no separate gate (AC-509).
    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        WindowActivation.BringToFront(_mainWindow);
    }

    // Really quits the app (tray "Quit") — lets MainWindow's close through, then the normal teardown runs.
    public void RequestQuit()
    {
        LifecycleLog.Write("Quit requested from inside the app (tray Quit or a restart handoff).");
        _ = TearDownThenShutdownAsync();
    }

    // Every quit route goes through here, not just ShutdownRequested: the lifetime's `Shutdown()` is a *forced*
    // shutdown that never raises that event, so the tray's Quit and the restart handoff would walk past it.
    internal async Task TearDownThenShutdownAsync()
    {
        // Before the first await, so MainWindow's close sees a real quit rather than a close-to-tray.
        IsQuitting = true;

        await (_teardown ??= Program.TearDownCockpitAsync());

        // Posted, not called: with nothing to tear down this returns inside the ShutdownRequested handler that just
        // cancelled, and re-entering the lifetime's shutdown from there is asking for it.
        Dispatcher.UIThread.Post(() => _desktop?.Shutdown());
    }

    // Keep the tray icon visible so support is obvious; only the setting changes close into hide (#33).
    // Otherwise it remains a Show/Quit shortcut and closing exits normally.
    private void _SetUpTrayIcon()
    {
        var showItem = new NativeMenuItem($"Show {CockpitProduct.DisplayName}");
        showItem.Click += (_, _) => ShowMainWindow();
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => RequestQuit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Cockpit.App/Assets/AppIcon.ico"))),
            ToolTipText = CockpitProduct.DisplayName,
            Menu = new NativeMenu { Items = { showItem, quitItem } },
        };
        tray.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, [tray]);
    }

    // Load workspaces before restoring their AI-session panes (AC-410). InitializeAsync never throws, so a failed
    // load continues safely with the in-memory default and nothing to restore.
    private static async Task _RestoreCockpitAsync(CockpitViewModel cockpit)
    {
        await cockpit.Workspaces.InitializeAsync();
        await cockpit.RestoreSessionPanesAsync();
    }

    // Starts the scheduled resumes (AC-234) and, unlike the bare fire-and-forget this replaces, watches how that
    // goes. A scheduler that failed to start is the one failure nobody notices by itself: nothing is on screen to
    // look wrong, and the first sign would be a resume that quietly never arrives, hours later (AC-368).
    private static async Task _StartScheduledResumesAsync(CockpitViewModel cockpit)
    {
        try
        {
            await cockpit.StartScheduledResumesAsync();
        }
        catch (Exception exception)
        {
            Program.Services.GetService<ILoggerFactory>()?.CreateLogger("Cockpit.App.ScheduledResumes")
                .LogError(exception, "Scheduled resumes could not be started; nothing that was scheduled will be sent.");

            // Said out loud, on the same host the startup toasts use: silence here is what AC-368 was.
            cockpit.ToastHost.Add(
                "Scheduled resumes are not running — anything scheduled will not be sent.",
                ToastSeverity.Error,
                null,
                null);
        }
    }

    // Builds the usage-threshold settings (AC-233) from what every registered provider declares — TTY and SDK
    // alike, since a provider can offer either route and declares the same signals for both — and hands the saved
    // values to the cockpit so sessions started from here judge their figures by them.
    private static async Task _LoadUsageThresholdsAsync(CockpitViewModel cockpit, IUsageThresholdStore store)
    {
        var providers = new List<(string ProviderId, string DisplayName, IReadOnlyList<PluginUsageSignal> Signals)>();

        foreach (var registration in Program.Services.GetService<IPluginTtyProviderRegistry>()?.Registrations ?? [])
        {
            providers.Add((registration.ProviderId, registration.DisplayName, registration.UsageSignals));
        }

        foreach (var registration in Program.Services.GetService<IPluginProviderRegistry>()?.Registrations ?? [])
        {
            // A provider registered on both routes declares the same signals for each; list it once.
            if (!providers.Any(entry => string.Equals(entry.ProviderId, registration.ProviderId, StringComparison.OrdinalIgnoreCase)))
            {
                providers.Add((registration.ProviderId, registration.DisplayName, registration.UsageSignals));
            }
        }

        var settings = new UsageThresholdsViewModel(store);
        await settings.LoadAsync(providers);

        cockpit.UsageThresholdSettings = settings;
        cockpit.UsageThresholds = await store.LoadAsync();
    }
}
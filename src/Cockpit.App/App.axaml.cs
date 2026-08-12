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
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Configuration;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
using Cockpit.Core.Toasts;
using Cockpit.Plugins.Abstractions.Workflows;

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
            desktop.ShutdownRequested += (_, _) =>
                LifecycleLog.Write($"The desktop lifetime requested shutdown (quit already requested: {IsQuitting}).");

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

    // The wizard stands in for the main window the same way `_ShowUnlockWindow`'s own unlock window
    // does: it is the app's only window until it is done, and the real cockpit is built and shown before this one
    // closes — never the other way round — so the desktop lifetime never sees zero windows open.
    // Built directly here rather than through `IFirstRunWizard`: that interface's contract is "show, wait for
    // the operator, and only then return" — which for the Help menu's "Run setup again" (AC-512, cockpit already
    // running) is exactly right, but here would close the wizard window before this method ever got a chance to
    // build the cockpit's, reopening the same zero-window gap one step later. Hooked on `Closing` rather
    // than the view model's `RequestClose` so every way of leaving reaches it uniformly — Skip, Next on the
    // last step, and the operator's own close button all end up closing the window, and `Closing` fires
    // before any of them actually do.
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

        // Explicit, the same reason _StartCockpit's own comment gives for _mainWindow.Show(): the framework only
        // auto-shows the very first MainWindow assignment at startup. When this replaces UnlockWindow instead (the
        // locked-at-startup route through _ShowUnlockWindow's Unlocked handler), that first assignment was
        // already the unlock window's, and nothing shows this one without an explicit call.
        window.Show();
    }

    // The startup probe reads cockpit.json through the same retry the settings stores use, so a save publishing at
    // that exact moment no longer throws (review #9). Should the read fail anyway — a genuinely unreadable file —
    // this must not crash the launch before a single window is up: fall through to a normal start, where the stores'
    // own backup-recovery and refusal handle a broken config the way they do everywhere else, with a window to say
    // so. Reading it as "locked" instead would send the operator to an unlock window backed by the same failing read.
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

    // Locks the running cockpit's UI (AC-5): shows the unlock window over the main window, so the app behind it cannot
    // be touched until the encryption password is entered again — the running-app twin of the startup unlock window
    // being the only window. This is a pure UI lock: the encryption key stays in memory, so agents already running
    // keep working (a background config write is not blocked) while the screen re-asks for the password. The returned
    // task completes when the operator has unlocked, which is what lets a later OS lock lock again. Runs on the UI
    // thread (the coordinator marshals here), and is idempotent through that coordinator, not on its own — a second
    // call while the dialog is up would try to own a second modal, which the guard prevents.
    private async Task _LockToUnlockScreen()
    {
        if (_mainWindow is null)
        {
            return;
        }

        // AC-509 Show()-inventory (2 of 3): _mainWindow already exists here, which only happens after
        // _StartCockpitAndOnboard has already run once this session — the onboarding gate is already resolved by
        // the time this route is reachable, so it needs no gate of its own.
        //
        // Bring the cockpit to the front first: a lock screen hidden behind a minimized or tray-hidden window reads
        // as a freeze, not as a lock. ShowDialog also needs a shown owner.
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

        // The lock is modal over the main window, and modality holds an owner rather than that owner's siblings.
        // Since AC-367 the work surfaces are siblings, so a locked cockpit still had options, MCP servers, the
        // plugin store and every plugin window open beside it. Hidden for the duration and put back on unlock, so
        // the lock covers the whole app without throwing away what was being filled in.
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

        // The New-session profile picker's ProfileDisplayConverter is used via x:Static (not DI-constructed), so
        // hand it the provider registry once here — that lets a plugin profile show its own provider's name (e.g.
        // "Claude") in the dropdown instead of the generic "(Plugin)" placeholder. The bundled plugins have already
        // registered by now, so the lookup resolves.
        Converters.ProfileDisplayConverter.PluginProviderRegistry =
            Program.Services.GetRequiredService<Cockpit.Infrastructure.Sessions.IPluginProviderRegistry>();

        _mainWindow = new MainWindow
        {
            DataContext = cockpitViewModel,
        };
        desktop.MainWindow = _mainWindow;

        // A closed window can never be shown again — Avalonia throws "Cannot re-show a closed window", and coming
        // from a tray-icon handler that exception took the whole cockpit down with it. Forgetting the window here
        // is what makes every route that shows it (the tray click, the tray menu's Show, the UI lock) fall through
        // the null check each already has, instead of each having to learn what a closed window looks like.
        _mainWindow.Closed += (_, _) => _mainWindow = null;

        // AC-509 Show()-inventory (1 of 3): the one route where _mainWindow is created — every call to
        // _StartCockpit goes through _StartCockpitAndOnboard, which is the gate (see its own remarks).
        //
        // Shown here rather than left to the lifetime: when this replaces the unlock window, the framework has
        // already shown its MainWindow and will not show a second one on its own.
        _mainWindow.Show();
        _SetUpTrayIcon();

        // Adopt the saved workspaces, then bring back the AI-session panes they name (AC-410). Fire-and-forget:
        // the view model already holds the default single Sessions workspace, so the window renders today's
        // cockpit immediately and the saved set — panes included — swaps in as the reads complete, rather than
        // the window waiting on file IO to appear. Chained rather than two separate fire-and-forgets: restoring
        // panes reads Workspaces.Settings, so it must not run until that load has actually landed.
        _ = _RestoreCockpitAsync(cockpitViewModel);

        // The feature coordinators are resolved for their constructors: each subscribes to the hotkey
        // coordinator there, and a singleton nobody asks for is never built — so an unresolved one would
        // simply not be listening when its key fires.
        Program.Services.GetRequiredService<VoicePushToTalkCoordinator>();

        // The assistant's own key (AC-543). Resolved for the same reason — the constructor is where it starts
        // listening — and then pointed at the Options page, so a rebound key or the feature being switched off
        // re-arms straight away instead of at the next restart. Resolving it starts no assistant: the instance is
        // built on the first hold or the first click on the chip, never before.
        var assistantPushToTalk = Program.Services.GetRequiredService<AssistantPushToTalkCoordinator>();

        // Read the assistant's switch off disk once at startup. Without this the host sits on its constructed
        // default — "switched off" — until something happens to save Options, so on every launch after the one
        // where it was turned on, the first F10 refused with a reason that was simply out of date. Starts
        // nothing: it resolves availability, and the first hold or click is still what builds the instance.
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

        // AC-575: the consent bypass holds its switches as a snapshot, because the broker asks it synchronously in
        // the middle of deciding and the store reads a file. Re-read here on the same Saved event the hotkey and
        // the chip already follow, so a source the operator just switched off stops being bypassed on the next
        // request rather than at the next restart. The singleton is the one the broker was handed.
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

        // AC-5: lock the cockpit's UI when the OS screen locks — put the unlock screen in front and ask for the
        // encryption password again — but only when encryption is on and the operator left the option on. A pure UI
        // lock: the key stays in memory so running agents keep working. The coordinator owns that gate and the
        // idempotence; App owns the windows, so it supplies how to show the unlock screen over the running cockpit.
        // Its task completes when the operator has unlocked again.
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

        // AC-234: hand the running app its scheduler — resolved here rather than through the view-model's
        // constructor, so the test and design-time graphs build a cockpit without one and never write to disk.
        // Before the plugins, deliberately: a session takes its copy of this when it is built, and one built while
        // this is still null never gets a second chance to hear about a resume waiting on it.
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

        // #59: one check right after plugin phase-2 (so a freshly discovered installed version is what
        // gets compared), then every 15 minutes for the rest of the run.
        // #71: and the cockpit itself. One look on startup, if the operator left that on — an update nobody is
        // told about is an update nobody installs. It never nags: a failed check is silent here, and only says
        // what went wrong when someone asks from Options.
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

        // Which storage keys hold a credential, before any plugin's settings are read: a value under a key the
        // host does not know to be a secret would be handed to the plugin as ciphertext, and left in a backup that
        // says it carries no credentials. The plugins declare them (plugin.json / SetSecret); the names are not
        // secrets themselves, so they are read without needing the key.
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

        // AC-403: move an OAuth token an older build filed under a server's name onto the id that server is known by
        // now. Here, at the tail of plugin phase 2, because a plugin's own connections are the only ones that need
        // it and this is the first moment they can be asked for.
        // Blocking, like the two settings reads at the top of this method, and for a stronger reason than either:
        // the main window is already on screen by now, so an await here would hand the operator a window they could
        // click in while the migration was still running — and a status read that lands in that gap says "sign-in
        // needed" about a credential that is present and about to be moved. The UI thread is inside this method for
        // the whole of plugin phase 2, so nothing can be clicked until it returns. It never throws (it logs), and
        // after the launch that migrates it is one small config read that writes nothing.
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

    // Restores and focuses the main window (tray left-click / the tray menu's "Show …" entry).
    // AC-509 Show()-inventory (3 of 3): reachable only once `_mainWindow` is non-null, i.e. after
    // `_StartCockpitAndOnboard` already ran this session — same reasoning as `_LockToUnlockScreen`,
    // no gate needed here.
    public void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            return;
        }

        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    // Really quits the app (tray "Quit") — lets MainWindow's close through, then the normal teardown runs.
    public void RequestQuit()
    {
        LifecycleLog.Write("Quit requested from inside the app (tray Quit or a restart handoff).");
        IsQuitting = true;
        _desktop?.Shutdown();
    }

    // A tray icon is always present while the app runs so the operator can immediately see whether the
    // tray works on their desktop (on GNOME/Wayland a legacy tray may need an AppIndicator extension).
    // Only when the "minimize to tray on close" setting is on does closing hide to it (#33) — otherwise
    // the tray is just a quick Show/Quit affordance and closing quits as usual.
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

    // Loads the saved workspaces and then brings back the AI-session panes they name (AC-410), in that order:
    // `WorkspacesViewModel.InitializeAsync` never throws (its own doc says so), so this continuation
    // always runs — including after a failed load, where `Settings` stays the in-memory default and there is
    // simply nothing saved to restore.
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
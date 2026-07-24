using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Plugins;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Plugins;
using Cockpit.Core.Secrets;
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
    private DispatcherTimer? _pluginUpdateTimer;

    /// <summary>
    /// True once a real quit was requested (tray "Quit"), so <see cref="MainWindow"/> lets the close
    /// through instead of hiding to tray (#33). Distinguishes a genuine quit from a close-to-tray.
    /// </summary>
    public bool IsQuitting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;

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

            _StartCockpit(desktop);
        }

        base.OnFrameworkInitializationCompleted();
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

    /// <summary>
    /// The unlock window is the app's only window until the password is right. It is the lifetime's MainWindow so
    /// the framework shows it; the real one replaces it, and is shown before this one closes — a moment with no
    /// window at all is a moment the desktop lifetime reads as "the app is done".
    /// </summary>
    private void _ShowUnlockWindow(IClassicDesktopStyleApplicationLifetime desktop, ISecretProtectionService protection)
    {
        var viewModel = new UnlockViewModel(protection);
        var window = new UnlockWindow { DataContext = viewModel };

        viewModel.Unlocked += (_, _) =>
        {
            _StartCockpit(desktop);
            window.Close();
        };

        desktop.MainWindow = window;
    }

    /// <summary>
    /// Locks the running cockpit's UI (AC-5): shows the unlock window over the main window, so the app behind it cannot
    /// be touched until the encryption password is entered again — the running-app twin of the startup unlock window
    /// being the only window. This is a pure UI lock: the encryption key stays in memory, so agents already running
    /// keep working (a background config write is not blocked) while the screen re-asks for the password. The returned
    /// task completes when the operator has unlocked, which is what lets a later OS lock lock again. Runs on the UI
    /// thread (the coordinator marshals here), and is idempotent through that coordinator, not on its own — a second
    /// call while the dialog is up would try to own a second modal, which the guard prevents.
    /// </summary>
    private Task _LockToUnlockScreen()
    {
        if (_mainWindow is null)
        {
            return Task.CompletedTask;
        }

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

        return window.ShowDialog(_mainWindow);
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

        // Shown here rather than left to the lifetime: when this replaces the unlock window, the framework has
        // already shown its MainWindow and will not show a second one on its own.
        _mainWindow.Show();
        _SetUpTrayIcon();

        // Adopt the saved workspaces. Fire-and-forget: the view model already holds the default single
        // Sessions workspace, so the window renders today's cockpit immediately and the saved set swaps in
        // when the read completes, rather than the window waiting on file IO to appear.
        _ = cockpitViewModel.Workspaces.InitializeAsync();

        // Fire-and-forget (#34): a no-op when voice or global push-to-talk is off, so the
        // portal/keyboard-hook is only ever touched for an operator who opted in.
        _ = Program.Services.GetRequiredService<VoicePushToTalkCoordinator>().StartAsync();

        // AC-5: lock the cockpit's UI when the OS screen locks — put the unlock screen in front and ask for the
        // encryption password again — but only when encryption is on and the operator left the option on. A pure UI
        // lock: the key stays in memory so running agents keep working. The coordinator owns that gate and the
        // idempotence; App owns the windows, so it supplies how to show the unlock screen over the running cockpit.
        // Its task completes when the operator has unlocked again.
        var screenLock = Program.Services.GetRequiredService<ScreenLockCoordinator>();
        screenLock.LockAction = () => Dispatcher.UIThread.InvokeAsync(_LockToUnlockScreen);
        _ = screenLock.StartAsync();

        // Open-mic dictation: expose the coordinator so the sidebar toggle can turn it on/off at
        // runtime, and resume listening at startup if it was left on. No-op when voice is off.
        var openMicCoordinator = Program.Services.GetRequiredService<OpenMicCoordinator>();
        cockpitViewModel.OpenMic = openMicCoordinator;
        _ = openMicCoordinator.StartAsync();

        // #14 Plugins — phase 2: now the container and the cockpit view model exist, hand each loaded
        // plugin the host built for it so it can register its Options tab / side-menu section.
        _InitializePlugins();

        // Silent unless the operator is carrying a plugin this build has replaced, in which case they are told
        // and asked — rather than having it cleaned out of their plugins folder behind their back.
        _ = Program.Services.GetRequiredService<SupersededPluginNotice>().CheckAsync();

        // #59: one check right after plugin phase-2 (so a freshly discovered installed version is what
        // gets compared), then every 15 minutes for the rest of the run.
        // #71: and the cockpit itself. One look on startup, if the operator left that on — an update nobody is
        // told about is an update nobody installs. It never nags: a failed check is silent here, and only says
        // what went wrong when someone asks from Options.
        _ = cockpitViewModel.InitialiseUpdatesAsync();
        // AC-188: and keep looking every hour after that, so a window left open for a workday still learns about a
        // build cut hours later. Reuses the same toast/banner/dedup path; stopped when the view model disposes.
        cockpitViewModel.StartPeriodicUpdateChecks();

        // AC-234: hand the running app its scheduler — resolved here rather than through the view-model's
        // constructor, so the test and design-time graphs build a cockpit without one and never write to disk.
        cockpitViewModel.ScheduledResumes = Program.Services.GetService<ScheduledResumeCoordinator>();
        _ = cockpitViewModel.StartScheduledResumesAsync();

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

        pluginManager.Initialize(discovered => new CockpitHost(
            discovered.FolderId,
            discovered.Manifest.Name,
            Program.Services,
            cockpit,
            actions,
            _CreatePluginStorage(discovered, registrationStore, secretFieldStore),
            dialogHost,
            sessionObserver,
            // The keys this plugin says hold a credential. They already gate encryption and the backup scrubber;
            // handing them to the host lets a dashboard export drop them too, which is the third place a
            // declared secret has to be honoured.
            discovered.Manifest.SecretKeys));

        // The templates installed from a store (#69) join the ones the plugins ship, in the same registry: to the
        // operator "a flow somebody already drew" is one kind of thing, whether it came with a plugin or from a store.
        // Read after the plugins have registered theirs, so an id clash is the store's copy losing to the plugin's own.
        _RegisterInstalledTemplates(
            Program.Services.GetRequiredService<IWorkflowTemplateLibrary>(),
            Program.Services.GetRequiredService<IWorkflowTemplateRegistry>());

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

    /// <summary>Restores and focuses the main window (tray left-click / "Show AI-Cockpit").</summary>
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

    /// <summary>Really quits the app (tray "Quit") — lets MainWindow's close through, then the normal teardown runs.</summary>
    public void RequestQuit()
    {
        IsQuitting = true;
        _desktop?.Shutdown();
    }

    // A tray icon is always present while the app runs so the operator can immediately see whether the
    // tray works on their desktop (on GNOME/Wayland a legacy tray may need an AppIndicator extension).
    // Only when the "minimize to tray on close" setting is on does closing hide to it (#33) — otherwise
    // the tray is just a quick Show/Quit affordance and closing quits as usual.
    private void _SetUpTrayIcon()
    {
        var showItem = new NativeMenuItem("Show AI-Cockpit");
        showItem.Click += (_, _) => ShowMainWindow();
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => RequestQuit();

        var tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Cockpit.App/Assets/AppIcon.ico"))),
            ToolTipText = "Cockpit",
            Menu = new NativeMenu { Items = { showItem, quitItem } },
        };
        tray.Clicked += (_, _) => ShowMainWindow();

        TrayIcon.SetIcons(this, [tray]);
    }

    /// <summary>
    /// Builds the usage-threshold settings (AC-233) from what every registered provider declares — TTY and SDK
    /// alike, since a provider can offer either route and declares the same signals for both — and hands the saved
    /// values to the cockpit so sessions started from here judge their figures by them.
    /// </summary>
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
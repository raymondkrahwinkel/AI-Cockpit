using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.App;

/// <summary>
/// Headless startup mode that renders a window off-screen via the Avalonia Skia headless platform and
/// writes a single frame to disk as PNG. Lets an external caller verify the UI layout without a display
/// attached (Iron Law #9: automated visual verification). <paramref name="scene"/> picks which window:
/// the main cockpit by default, or a dialog whose layout would otherwise be unverifiable.
/// </summary>
internal static class Screenshotter
{
    private const int DefaultWindowWidth = 1100;
    private const int DefaultWindowHeight = 760;

    public static void Run(string outputPngPath, int width = DefaultWindowWidth, int height = DefaultWindowHeight, string? scene = null, string? snapshotPath = null, string? snapshotTarget = null)
    {
        BuildHeadlessAvaloniaApp().SetupWithoutStarting();

        var window = ShowScene(scene, width, height);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless renderer produced no frame to capture.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPngPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        frame.Save(outputPngPath);

        if (!string.IsNullOrEmpty(snapshotPath))
        {
            _WriteSnapshot(window, snapshotPath, snapshotTarget);
        }

        window.Close();
    }

    /// <summary>
    /// The window each scene name asks for. A table rather than a switch so the set of names can be read off it:
    /// anything that has to cover every screen — the theme baseline (AC-338) above all — would otherwise be
    /// working from a hand-written list, and a hand-written list is blind to exactly the scene nobody remembered.
    /// </summary>
    private static readonly Dictionary<string, Func<int, int, Window>> Scenes = new(StringComparer.Ordinal)
    {
        ["about"] = (_, _) => new AboutDialog { DataContext = ViewModels.AboutInfo.FromAssembly(typeof(Screenshotter).Assembly) },
        ["single-instance"] = (_, _) => new SingleInstanceNoticeDialog(),
        ["options"] = (_, _) => new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() },
        ["shortcuts"] = (_, _) => _OptionsOnTab("Shortcuts"),
        ["debug"] = (_, _) => _OptionsOnTab("Debug"),
        ["profiles"] = (_, _) => new ManageProfilesDialog { DataContext = new ViewModels.ManageProfilesDialogViewModel(), Height = 900 },
        // The Default kind editor (AC-139) in each of its three states: a Claude profile (has a TTY route) with
        // the toggle pre-set to TTY, the same profile with it pre-set to SDK, and a local-provider profile (no TTY
        // route at all) where the toggle disappears in favour of a plain "SDK-only" label. Tall enough that the
        // section — well below the fold of the editor's resting height — is not the part of the scene you cannot see.
        ["profiles-default-kind-tty"] = (_, _) => _ManageProfilesWithDefaultKind(SessionKind.Tty),
        ["profiles-default-kind-sdk"] = (_, _) => _ManageProfilesWithDefaultKind(SessionKind.Sdk),
        ["profiles-default-kind-sdk-only"] = (_, _) => _ManageProfilesSdkOnlyDefaultKind(),
        ["verify-runners"] = (_, _) => new VerifyRunnersDialog { DataContext = new ViewModels.VerifyRunnersViewModel() },
        ["verify-runners-edit"] = (_, _) => _VerifyRunnersEditing(),
        ["new-session"] = (_, _) => new NewSessionDialog { DataContext = new ViewModels.NewSessionDialogViewModel() },
        // The project editor and the projects window, both of which show a project's own information rows
        // (AC-295) — the editor as key/value boxes, the window as the label-over-value block a card shows.
        ["project-editor"] = (_, _) => new ProjectDialog { DataContext = new ViewModels.ProjectDialogViewModel() },
        // The same editor once a plugin contributes a memory source (AC-165/166). Its own scene because the Memory
        // row only grows its picker when something is registered, and nothing is in a plain render: without this the
        // one state the operator sees differently is the one state that cannot be looked at.
        ["project-editor-memory-source"] = (_, _) => _ProjectEditorWithMemorySource(),
        ["project-editor-resources"] = (_, _) => _ProjectEditorWithResources(),
        // AC-499: the server row's own two states — a family with instances to pick from (its dropdown), and a
        // family with none yet (its empty hint plus "Servers…" in the dropdown's place) — staged together since
        // only one row of each is needed to prove both render, and DialogScreenClamp caps how much of this dialog
        // any one scene can show anyway (see _ProjectEditorWithResources's own remarks on that ceiling).
        ["project-editor-memory-source-families"] = (_, _) => _ProjectEditorWithMemorySourceFamilies(),
        // AC-502: the "Choose…" picker itself, in the state that paints the most of its own surface — a loaded
        // list with a two-line entry (name plus its detail) — plus the two states that must never read as an empty
        // list (not signed in, and a failed load), each its own scene since only one of the four states shows at a
        // time and a resting-state screenshot would otherwise never catch the other three regressing silently.
        ["memory-source-location-picker"] = (_, _) => _MemorySourceLocationPicker(),
        ["memory-source-location-picker-sign-in"] = (_, _) => _MemorySourceLocationPickerSignIn(),
        ["memory-source-location-picker-error"] = (_, _) => _MemorySourceLocationPickerError(),
        // AC-499: the row the operator already had, pre-selected and marked — a longer list (ten locations, one
        // without a Detail line) than the plain scene above so the current row (seventh) needs an actual scroll
        // into view, and the ragged-row-height question (a Detail-less row among Detail-carrying ones) is on
        // screen rather than assumed away.
        ["memory-source-location-picker-current"] = (_, _) => _MemorySourceLocationPickerCurrent(),
        // AC-499: the other half of the same case — a Reference the picker's list does not contain (removed,
        // mistyped, no longer visible to this login). Nothing selected; see the view model's own remarks on why a
        // stale value never falls back to picking something else.
        ["memory-source-location-picker-current-missing"] = (_, _) => _MemorySourceLocationPickerCurrentMissing(),
        // AC-503: the three states a Memory row's own reachability check can land on — confirmed, not found, and
        // not signed in/unreachable — staged directly (see _ProjectEditorWithMemorySourceReachability's own
        // remarks on why this scene sets Reachability rather than going through a real check delegate).
        ["project-editor-memory-source-reachability"] = (_, _) => _ProjectEditorWithMemorySourceReachability(),
        // A row's "Send along" checkbox (AC-486) — its own scene rather than a third row in the one above; see
        // _ProjectEditorWithInstructionsSendAlong's own remarks on why combining the two cost the resources scene
        // a hint it had already proved visible.
        ["project-editor-instructions-send-along"] = (_, _) => _ProjectEditorWithInstructionsSendAlong(),
        ["projects"] = (_, _) => new ProjectsDialog { DataContext = ViewModels.ProjectsViewModel.DesignSample() },
        ["plugin-store"] = (_, _) => _PluginStore(),
        // The store's two busy states (AC-420) — otherwise only reachable while a real download is in flight.
        ["plugin-store-installing"] = (_, _) => _PluginStoreBusy(percent: null, "Downloading 'GitHub Issues' v1.8.0…"),
        ["plugin-store-updating"] = (_, _) => _PluginStoreBusy(percent: 200.0 / 6, "Updating 'Git status' (3 of 6)…"),
        ["manage-stores"] = (_, _) => _ManageStores(),
        ["tasks"] = (_, _) => new DelegatedTasksDialog { DataContext = new ViewModels.DelegatedTasksViewModel() },
        ["set-status"] = (_, _) => new SetStatusDialog { DataContext = new ViewModels.SetStatusDialogViewModel("AC-32 — manual status") },
        ["session"] = (_, _) => new MainWindow { DataContext = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true } },
        // AC-543 criterion 11: the assistant chip in the sidebar it actually lives in, expanded and as the rail.
        // The component has scenes of its own below, but those render it alone — what neither of them can show is
        // whether it survives the collapse in place, which is the half of criterion 6 that is about the sidebar
        // rather than about the chip.
        // Every chip state at once, at the width the sidebar actually gives it. One scene rather than seven,
        // because what goes wrong at this size is comparative — a label that wraps mid-word, a key hint that
        // squeezes the text out, one state sitting taller than its neighbours — and none of that is visible in a
        // render of a single state in a roomy window. Raymond's idea, after two rounds of exactly those defects
        // reaching him instead of me.
        ["assistant-indicator-all-states"] = (_, _) => _AssistantIndicatorGallery(),
        ["sidebar-assistant"] = (_, _) => _SidebarWithAssistant(collapsed: false),
        ["sidebar-assistant-rail"] = (_, _) => _SidebarWithAssistant(collapsed: true),
        // Off means no chip at all, not a chip reporting that it is off (AC-542's own wording). Its own scene
        // because the absence is the thing being attested to, and absence is exactly what a passing test suite
        // looks like when nobody ever rendered it.
        ["sidebar-assistant-off"] = (_, _) => _SidebarWithAssistant(collapsed: false, featureEnabled: false),
        // A sub-agent's own activity nested under its parent Task tool-use row (AC-146), collapsed (the default an
        // operator meets) and expanded — the verbosity a collapsed default guards against is exactly the thing
        // this ticket's own acceptance criteria demanded be eyeballed on screen, not just asserted in a test.
        ["session-subagent"] = (width, height) => new Window { Width = width, Height = height, Content = _SubAgentSession(expanded: false) },
        ["session-subagent-expanded"] = (width, height) => new Window { Width = width, Height = height, Content = _SubAgentSession(expanded: true) },
        // AC-558: the transcript lines Raymond reported — a bare URL, and a link wrapped in bold that used to
        // print its own markdown syntax on screen. Rendered rather than only asserted on the parser, because
        // "a link is there" and "it reads as a link, and the one next to it is a different link" are separate
        // claims and only the second is visible.
        ["session-links"] = (width, height) => new Window { Width = width, Height = height, Content = _LinkTranscript() },
        ["session-settings-flyout"] = (width, height) => new Window { Width = width, Height = height, Content = _SessionSettingsFlyout(withLiveControls: true) },
        ["session-settings-flyout-no-live-controls"] = (width, height) => new Window { Width = width, Height = height, Content = _SessionSettingsFlyout(withLiveControls: false) },
        // AC-563, staged open by the Hovers table below. See _McpHeader for why each of these four is its own.
        ["session-kind-chip-hover"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader(servers: new HashSet<string> { "youtrack", "depot" }) },
        ["session-mcp-hover"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader(servers: new HashSet<string> { "youtrack", "depot", "cockpit-local-ci", "github-issues" }) },
        ["session-mcp-hover-statusline"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader("AC-563 — wiring the header hover", new HashSet<string> { "youtrack", "depot", "cockpit-local-ci", "github-issues" }) },
        ["session-mcp-hover-unknown"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader() },
        ["tty"] = (width, height) => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = new ViewModels.TtyViewModel() } },
        // A plain terminal pane (#AC-25/#AC-29): its own scene so the shared header's terminal treatment
        // (kind chip "TTY", no plugin host, no usage pill, shell name only in the cwd tooltip) is verifiable
        // headless — the SDK-only 'session' scene is exactly what let the earlier TTY-header miss slip through.
        ["terminal"] = (width, height) => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = ViewModels.TtyViewModel.DesignTerminal() } },
        // The restore offer a pane comes back with after a crash (AC-410), in both the states that paint
        // differently and on both views that carry the banner. Two scenes because the resumable case hides the
        // degraded reason and the degraded case hides the Resume button, so neither renders the other's surface;
        // two views because SessionView and TtyView each spell the banner out separately, and a banner added to
        // only one of them is exactly the kind of half-landed change a single scene would attest to as finished.
        ["restore-offer"] = (width, height) => _RestorePane(width, height, degraded: false),
        ["restore-offer-degraded"] = (width, height) => _RestorePane(width, height, degraded: true),
        ["mcp-servers"] = (_, _) => _McpServers(),
        // AC-499: Sign in's three new states, each its own scene because only the selected row's detail panel
        // renders at a time — a list with rows in different states never puts more than one of these on screen
        // together. See _McpServersSignInUnsaved's own remarks for what each state replaced.
        ["mcp-servers-signin-unsaved"] = (_, _) => _McpServersSignInUnsaved(),
        ["mcp-servers-signin-invalid"] = (_, _) => _McpServersSignInInvalid(),
        ["mcp-servers-signin-busy"] = (_, _) => _McpServersSignInBusy(),
        ["plugin-update-badge"] = (_, _) => _PluginUpdateBadge(),
        ["toolbar-actions"] = (_, _) => _ToolbarActions(),
        // The windows that had no scene at all (AC-414). Each is staged in the state that paints the most of
        // itself, because a dialog rendered in its resting state records the colours of an empty form and leaves
        // exactly the surfaces nobody can eyeball — an error line, a filled list — out of its own baseline.
        ["clone-from-git-url"] = (_, _) => _CloneFromGitUrl(),
        ["command-palette"] = (_, _) => _CommandPalette(),
        ["confirmation"] = (_, _) => new ConfirmationDialog
        {
            DataContext = new ViewModels.ConfirmationDialogViewModel(
                "Remove store", "Remove 'AI-Cockpit Plugins'? The plugins you installed from it stay where they are.", "Remove"),
        },
        // Asked for while changing a password, which is the variant with the extra field: the same dialog with
        // one fewer box paints nothing this one does not.
        ["password"] = (_, _) => new PasswordDialog
        {
            DataContext = new ViewModels.PasswordDialogViewModel(
                "Change your password", "Type the password you use now, then the one you want.", requiresCurrent: true),
        },
        ["plugin-consent"] = (_, _) => new PluginConsentDialog
        {
            DataContext = new ViewModels.PluginConsentInfo(
                "GitHub Issues", "1.8.0", "Cockpit", "/home/you/.config/Cockpit/plugins/github-issues",
                "9f2c4b1ea7d05836c1b4e0f9a3d7c25e8b6041fd93a7e2c5b80d1a6a4e37c9b2"),
        },
        ["restore-selection"] = (_, _) => _RestoreSelection(),
        ["schedule-resume"] = (_, _) => new ScheduleResumeDialog { DataContext = new ViewModels.ScheduleResumeDialogViewModel() },
        // With the password rejected, because the error line is the only thing on this window that is not always
        // there — and the window stands in front of a cockpit nobody can reach past it to look.
        ["unlock"] = (_, _) => new UnlockWindow
        {
            DataContext = new ViewModels.UnlockViewModel { Error = "That password does not unlock these credentials." },
        },
        ["worktrees"] = (_, _) => new WorktreesDialog { DataContext = new ViewModels.WorktreesViewModel() },
        // The voice pill's rows are mutually exclusive, so one scene shows one row — and a row with no scene
        // cannot be rendered at all, because a render is asked for by name and nothing walks this table. All five,
        // therefore, rather than only the three that paint ink the others do not: speaking and unavailable would
        // each produce a baseline that repeats another file, but that is a reason to keep a duplicate file, not a
        // reason to leave a state of a window unlookable-at (the argument is in ThemePaletteBaselineTests).
        ["voice-overlay-listening"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Listening),
        ["voice-overlay-preparing"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Preparing, "Downloading the speech model (1.1 of 1.6 GB)", progress: 0.7),
        ["voice-overlay-transcribing"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Transcribing),
        ["voice-overlay-speaking"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Speaking),
        ["voice-overlay-unavailable"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Unavailable, "Open mic is on"),

        // AC-543 (strand 3): the reusable assistant indicator (AssistantIndicator.axaml), a row per acceptance
        // criterion 11 — "a row with no scene is a row nobody looks at". All seven AssistantActivity states
        // expanded, the same badge collapsed to its rail form (criterion 6/19), and the three listening-mode
        // stands (criteria 17/18) including the one-time AlwaysOn cost confirmation. Wrapped in a small fixed
        // Window rather than the default 1100x760 (the "tty"/"terminal" scenes' own approach) because this
        // control is a sidebar chip, not a full pane — a full-size render would show mostly empty background.
        // The profile label is reused verbatim from ProfileDisplay.Format's own convention ("label (provider)")
        // rather than a bespoke string invented for this one scene — the second line reads the same way here as
        // everywhere else a profile is named.
        ["assistant-indicator-ready"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.Ready, profileLabel: "default (Claude CLI)"),
        ["assistant-indicator-listening"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Listening),
        ["assistant-indicator-listening-continuously"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously),
        ["assistant-indicator-thinking"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Thinking),
        ["assistant-indicator-speaking"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Speaking),
        // Dictating (F9) beside Listening (F10) above is the pair criterion 6 exists for — amber vs cyan, and
        // "Dictating" vs "Listening" in words, never only the colour.
        ["assistant-indicator-dictating"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Dictating),
        ["assistant-indicator-unavailable"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.Unavailable, unavailableReason: "No model on this machine"),
        // The rail form (criterion 6/19): collapsed while still listening continuously, so the mode dot that is
        // this state's whole point (Theme.axaml's Ellipse.assistantIndicatorModeDot remarks) is on screen, not
        // just the plain ring every other collapsed state would show identically.
        ["assistant-indicator-rail"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously,
            listeningMode: Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn, collapsed: true),
        ["assistant-indicator-listening-mode-always-on"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.Ready, listeningMode: Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn),
        // Criterion 18: the one-time AlwaysOn cost explanation, mid-flow — picked but not yet confirmed.
        ["assistant-indicator-always-on-confirm"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.Ready, alwaysOnConfirmationPending: true),

        // AC-543 (strand 4): the pop-out chat window (AssistantChatWindow.axaml), criterion 11's other half — the
        // indicator is a badge, this is where the conversation actually reads. Three states: a standing
        // conversation with a collapsed tool call in it (the existing SDK transcript rendering — markdown,
        // collapsible tool calls — reused, not rebuilt, per the ticket's hard requirement), the window before
        // anything has been said (criterion 7's "reads a conversation, it does not start one" has to hold even
        // here, on the very first open), and read-aloud switched off (criterion 9).
        ["assistant-chat"] = (_, _) => _AssistantChat(withConversation: true),
        ["assistant-chat-empty"] = (_, _) => _AssistantChat(withConversation: false),
        ["assistant-chat-speak-off"] = (_, _) => _AssistantChat(withConversation: true, speakReplies: false),

        // AC-566 criterion 8: the preview window gated behind Confirm(), with a wide screenshot and a narrow
        // one — the two extremes Stretch="Uniform" has to lay out, rather than only whatever aspect ratio a
        // developer's own screen happens to produce.
        ["screenshot-preview-wide"] = (_, _) => Views.ScreenshotPreviewWindow.Build(_StandInPng(1600, 500), "personal - webshop"),
        ["screenshot-preview-narrow"] = (_, _) => Views.ScreenshotPreviewWindow.Build(_StandInPng(500, 1400), "personal - webshop"),
    };

    /// <summary>
    /// Every scene name a render can be asked for, this table's own plus the selection surface's — that one keeps
    /// its names with the scene because its modes are states the surface is driven into after it is shown, not
    /// windows that open in them, so the name means nothing until then.
    /// </summary>
    internal static IReadOnlyList<string> SceneNames { get; } = [.. Scenes.Keys, .. ScreenshotSelectionScene.Names];

    /// <summary>
    /// The window a scene asks for, on screen and in the state the name describes. Its own step so that anything
    /// looking at a scene — a render here, the theme baseline in the view tests — reaches it by the same route,
    /// rather than a second copy that can drift out of step with this one.
    /// </summary>
    internal static Window ShowScene(string? scene, int width = DefaultWindowWidth, int height = DefaultWindowHeight)
    {
        var window = BuildScene(scene, width, height);
        window.Show();

        // The selection surface's modes are states an operator drives it into, not windows that open in them, so
        // the scene reaches them here — after the window is up and has a size to measure positions against.
        if (window is ScreenshotSelectionWindow surface)
        {
            ScreenshotSelectionScene.Stage(surface, scene);
        }

        if (Hovers.TryGetValue(scene ?? string.Empty, out var open))
        {
            open(window);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        return window;
    }

    /// <summary>
    /// Scenes whose subject is a flyout or a tooltip. Both attach to a host that does not exist until the window
    /// is up, so — like the selection surface's modes above — they are opened here rather than built into the
    /// scene. Headless renders them into the parent window's own frame, so the capture still shows them in place
    /// on the header they belong to.
    /// </summary>
    private static readonly Dictionary<string, Action<Window>> Hovers = new(StringComparer.Ordinal)
    {
        ["session-settings-flyout"] = window => _OpenFlyout(window, "SessionSettingsButton"),
        ["session-settings-flyout-no-live-controls"] = window => _OpenFlyout(window, "SessionSettingsButton"),
        ["session-kind-chip-hover"] = window => _OpenTooltip(window, "KindChip"),
        ["session-mcp-hover"] = window => _OpenTooltip(window, "ActivityColumn"),
        ["session-mcp-hover-statusline"] = window => _OpenTooltip(window, "ActivityColumn"),
        ["session-mcp-hover-unknown"] = window => _OpenTooltip(window, "ActivityColumn"),
    };

    private static void _OpenFlyout(Window window, string buttonName)
    {
        var button = _Named<Button>(window, buttonName);
        button.Flyout?.ShowAt(button);
    }

    private static void _OpenTooltip(Window window, string controlName) =>
        ToolTip.SetIsOpen(_Named<Control>(window, controlName), true);

    // By name over the whole rendered tree, because the header bar is a control of its own and its named parts
    // are not in the view's name scope — FindControl on the view would come back empty.
    private static T _Named<T>(Window window, string name)
        where T : Control
        => window.GetVisualDescendants().OfType<T>().First(control => control.Name == name);

    /// <summary>
    /// The window a scene name asks for, built and sized but not shown. Its own step so the table above can be
    /// held to a test — a scene that stopped building was otherwise found by whoever next asked for a render,
    /// which on this surface has meant finding it after it shipped. An unknown name falls back to the main
    /// window, so a render never fails on a typo — the tests are what hold the names.
    /// </summary>
    internal static Window BuildScene(string? scene, int width = DefaultWindowWidth, int height = DefaultWindowHeight)
    {
        Window window;
        if (scene is not null && Scenes.TryGetValue(scene, out var build))
        {
            window = build(width, height);
        }
        else if (ScreenshotSelectionScene.Covers(scene))
        {
            window = ScreenshotSelectionScene.Build(scene, width, height);
        }
        else
        {
            window = new MainWindow { DataContext = new ViewModels.CockpitViewModel() };
        }

        // A SizeToContent dialog measures itself; only the main window takes the requested size.
        if (window is MainWindow)
        {
            window.Width = width;
            window.Height = height;
        }

        return window;
    }

    private static void _WriteSnapshot(Visual root, string snapshotPath, string? target)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(snapshotPath, VisualTreeSnapshot.Capture(root, target));
    }

    // Renders the project editor as it looks with a memory source installed: the Memory row leads with the source
    // picker, the box holds the bare identifier rather than a path, and "Choose…" has stepped aside. Staged on the
    // view model directly rather than through a registry, because what this scene has to show is the row — the
    // wiring that fills the picker is covered by tests, and a scene that needed a plugin loaded to render would not
    // be renderable at all.
    private static ProjectDialog _ProjectEditorWithMemorySource()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel();
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot project", "depot"));

        // AC-485 folded the dialog's old standalone Memory row into the resource section below; this scene keeps
        // exercising the same picker (a Memory row with a source other than Folder selected) through that section
        // instead, one row rather than a dedicated field.
        var memoryRow = new ViewModels.ProjectResourceRowViewModel(viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "cockpit");
        memoryRow.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        viewModel.ResourceRows.Add(memoryRow);

        // Taller than the dialog opens, the way the profiles scene is: the resource section sits below the fold of
        // a default-sized editor, and a scene that renders the part you cannot see proves nothing about the part
        // this change is in.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    /// <summary>
    /// The resource section with two rows, in the state that paints the most of it (AC-414): a Memory row with a
    /// plugin source picked (mirroring the memory-source scene above, folded into this section instead of a
    /// dedicated field), and a Reference row that is both machine-bound and broken — the two things AC-485 requires
    /// be visible in the editor itself, not only in a prompt the operator never reads. Two rows rather than all
    /// three roles: <see cref="Controls.DialogScreenClamp"/> caps a dialog's height at 90% of the (headless) screen
    /// regardless of the <c>Height</c> set below, so a third row would sit past what any render of this scene can
    /// actually show — better two rows fully visible than three where the last is cut off mid-row. The
    /// broken/machine-bound flags are set directly rather than produced by running the real probe against real
    /// paths, so this scene paints the same way on every platform this repo builds on, regardless of what OS drew
    /// the screenshot.
    /// <para>
    /// AC-486 review: an earlier version of this scene changed this second row's own Role to Instructions and
    /// ticked "Send along" on it too, on the theory that neither probe looks at Role so nothing here would cost
    /// anything. Rendered, that combined row's own two extra lines (the checkbox column plus its hint text) pushed
    /// the machine-bound hint below this scene's own fold — the exact same <see cref="Controls.DialogScreenClamp"/>
    /// ceiling this doc comment already warns about, just reached one row sooner. That silently broke the very thing
    /// this scene's own AC-485 review already confirmed visible, without a single test noticing (a palette baseline
    /// permits a screen to paint fewer colours than it lists, and no test here asserts on vertical position). "Send
    /// along" gets <see cref="_ProjectEditorWithInstructionsSendAlong"/> instead, its own scene, precisely so it
    /// never has to compete with this one for the same limited fold.
    /// </para>
    /// </summary>
    private static ProjectDialog _ProjectEditorWithResources()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot project", "depot"));

        var memoryRow = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "cockpit", "Team notes");
        memoryRow.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        viewModel.ResourceRows.Add(memoryRow);

        // Reaching sessions, deliberately, even though an unticked row would show the checkbox contrast: a row that
        // is switched off is never probed at all, so "switched off" and "broken" cannot both be true of it. Staged
        // together they made this scene paint a state the app cannot produce — and the diagnostics pass corrects it
        // the moment it runs, which is how the red hint disappeared from the render without a single test noticing
        // (a palette baseline permits a screen to paint fewer colours than it lists).
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Reference, @"C:\Users\raymond\OldNotes\handbook.md", "Old handbook")
        {
            IsBroken = true,
            IsMachineBound = true,
        });

        // Taller than the dialog opens, the way the memory-source scene already is — clamped by DialogScreenClamp
        // to 90% of the headless screen either way, but still what pushes the resource section as far above that
        // ceiling as this dialog's other sections leave room for.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    /// <summary>
    /// AC-499's second axis, both of its states in one scene (Iron Law #9): a "Depot" family with two instances to
    /// choose between (its own dropdown, populated), and a "Notes vault" family with none registered yet (its
    /// dropdown's place taken by a disabled box carrying <see cref="ProjectMemorySourceFamily.EmptyHint"/>, with
    /// "Servers…" sitting beside it either way). Built directly against <see cref="ProjectResourceRowViewModel"/>'s
    /// own constructor — passing <c>familyInstanceChoicesByKey</c> straight in — rather than through
    /// <see cref="ProjectDialogViewModel.CreateAsync"/>: <c>MemorySourceFamilyInstances</c> only has a private
    /// setter, so a scene (outside the view model's own assembly boundary in every way but the CLR's) builds the
    /// same shape by hand, exactly as <see cref="_ProjectEditorWithResources"/> already does for
    /// <c>MemorySourceChoices</c> itself.
    /// </summary>
    private static ProjectDialog _ProjectEditorWithMemorySourceFamilies()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        // See _ProjectEditorWithMemorySourceReachability's own remarks on why the design-time sample rows are
        // cleared here too — every bit of chrome this scene does not need earns back room under the fold.
        viewModel.AdditionalInfo.Clear();

        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot", Scheme: null)
        {
            FamilyKey = "depot",
            EmptyHint = "No Depot server configured yet",
            ConfigureAsync = _ => Task.CompletedTask,
        });
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Notes vault", Scheme: null)
        {
            FamilyKey = "notes",
            EmptyHint = "No Notes vault configured yet",
            ConfigureAsync = _ => Task.CompletedTask,
        });

        var depotInstances = new List<ViewModels.MemorySourceChoice>
        {
            new("Depot (krahwinkel-it)", "depot"),
            new("Depot (synvolution)", "depot.synvolution"),
        };
        var familyInstances = new Dictionary<string, IReadOnlyList<ViewModels.MemorySourceChoice>>(StringComparer.OrdinalIgnoreCase)
        {
            ["depot"] = depotInstances,
            ["notes"] = [],
        };

        var withInstances = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "krahwinkel-it", "Team notes",
            familyInstanceChoicesByKey: familyInstances)
        {
            SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1],
        };
        withInstances.SelectedFamilyInstance = depotInstances[0];
        viewModel.ResourceRows.Add(withInstances);

        var empty = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "", "Personal notes",
            familyInstanceChoicesByKey: familyInstances)
        {
            SelectedMemorySourceChoice = viewModel.MemorySourceChoices[2],
        };
        viewModel.ResourceRows.Add(empty);

        // Taller than the dialog opens, the same reason every other resource-section scene already gives: two
        // rows' worth of picker plus server row sit well below the fold of a default-sized editor.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    // AC-502: a loaded picker, two locations so the list itself (not just a single row) is verifiable — one with
    // a detail line, one without, since ProjectMemorySourceLocation.Detail is optional and both must render cleanly.
    // AC-499: the two also carry different kinds (Project/Brain) in that same detail line — DepotMemorySource's own
    // _DetailFor puts the kind first — so the picker's own "which sort of place is this" distinction (Raymond's own
    // krahwinkel-it instance mixes Depot projects and Depot brains under one connection) is actually on screen.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPicker()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Synvolution",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success(
            [
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("cockpit", "Cockpit", "Project · 21 documents · updated 26 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("olaf", "Olaf", "Brain"),
            ])));
        // Loaded synchronously rather than left to the window's own fire-and-forget OnDataContextChanged call: a
        // static screenshot needs the list settled before the frame is captured, not racing a dispatcher tick.
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // The "not signed in" state (AC-502 criterion 4): one action, not an empty list — this is the scene that proves
    // it never renders as "you have nothing" on screen, not merely in a unit test's assertions.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerSignIn()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Synvolution",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.AuthorizationRequired),
            signInAsync: _ => Task.FromResult(true));
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // The failed-load state (AC-502 criterion 5): says what went wrong rather than showing nothing.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerError()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Synvolution",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Failed(
                "Couldn't reach the Depot server — check the connection's URL in Depot's settings.")));
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // AC-499: ten locations (Raymond's own krahwinkel-it instance mixes this many Depot projects and brains), the
    // seventh carrying the Reference the row already had — proves the pre-selection, its "Current" badge, the
    // scroll into view (this list does not fit the window's own resting height), and — via the one entry with no
    // Detail line — whether a Detail-less row still sits noticeably shorter than its neighbours.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerCurrent()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — krahwinkel-it",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success(
            [
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("cockpit", "Cockpit", "Project · 21 documents · updated 26 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("synvolution", "Synvolution", "Project · 8 documents · updated 20 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("ai-hub", "AI-Hub", "Project · 5 documents · updated 18 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("olaf", "Olaf", "Brain"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("zyra", "Zyra", "Brain"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("moneybird-toolbox", "Moneybird-Toolbox", "Project · 3 documents · updated 12 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("eve-workbench", "EVE Workbench", "Project · 42 documents · updated 29 Jul 2026"),
                // No Detail — right after the pre-selected row, so both land in the same scrolled-into-view
                // viewport and a height difference between them (point 3) is actually on screen, not assumed away.
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("ddd-template", "DDD-Template"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("payroll-processor", "PayrollProcessor", "Project · 14 documents · updated 22 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("vacancy-manager", "VacancyManager", "Project · 6 documents · updated 15 Jul 2026"),
            ])),
            currentValue: "eve-workbench");
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // AC-499: the Reference the row carries in ("moved-away") does not match any location this login can see —
    // removed, mistyped, or from a login that no longer has access. Nothing here should be selected; see the
    // view model's own remarks on why a miss never falls back to picking something else.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerCurrentMissing()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Synvolution",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success(
            [
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("cockpit", "Cockpit", "Project · 21 documents · updated 26 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("olaf", "Olaf", "Brain"),
            ])),
            currentValue: "moved-away");
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    /// <summary>
    /// AC-503, Iron Law #9: three of the four states a Memory row's own reachability check can land on, one row
    /// each — confirmed, not found, and not signed in — so they are actually visible on screen rather than only
    /// asserted in a test. AC-499 added a fourth (<c>CheckFailed</c> — the check ran but the call itself failed,
    /// kept apart from NotSignedIn precisely so a signed-in operator is never told to sign in again); not staged
    /// here too, only for the same fold reason the remark below already gives for not fitting a third row's full
    /// text — <see cref="ProjectResourceRowViewModel.IsCheckFailed"/>'s own XAML binding shares its brush
    /// (<c>CockpitStatusWaitingBrush</c>) with NotSignedIn, already proven legible by this very scene.
    /// <see cref="ProjectResourceRowViewModel.Reachability"/> is staged directly rather than through a real
    /// <see cref="ProjectMemorySourceRegistration.CheckReachability"/> delegate and a live dialog run, the same
    /// shortcut <see cref="_ProjectEditorWithResources"/> already takes for <c>IsBroken</c>/<c>IsMachineBound"</c>:
    /// what this scene exists to prove is the view's own state rendering, not the plugin/host wiring behind it,
    /// which the ViewModel and Depot-plugin test suites already cover on their own.
    /// </summary>
    private static ProjectDialog _ProjectEditorWithMemorySourceReachability()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel();
        // The design-time constructor's own sample "Repository"/"Customer" rows are cleared: three memory rows
        // each carrying their own picker and hint already push this scene's own fold further down than
        // _ProjectEditorWithResources's two rows do, and DialogScreenClamp still caps the window at 90% of the
        // headless screen no matter what Height this scene asks for (BuildTraps.md's own note on this) — every bit
        // of chrome this scene does not need earns back room for the states it exists to show.
        viewModel.AdditionalInfo.Clear();
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot project", "depot"));

        var confirmed = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "cockpit", "Team notes");
        confirmed.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        confirmed.Reachability = ProjectMemorySourceReachability.Confirmed;
        confirmed.ReachabilityDetail = "24 documents, last changed 2 hours ago";
        viewModel.ResourceRows.Add(confirmed);

        // Second, not third: DialogScreenClamp caps this dialog's own render at 90% of the headless screen no
        // matter how tall this scene asks for (BuildTraps.md), so a screenshot of this scene alone cannot show all
        // three states' full hint text below the fold either way. NotSignedIn (a colour — CockpitStatusWaitingBrush
        // — this dialog has never shown before AC-503) is placed where it is guaranteed to render in full; NotFound
        // reuses IsBroken's own established red/CockpitStatusErrorBrush treatment, which a render already proved
        // legible for this dialog before this ticket existed.
        var notSignedIn = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "wispslate", "Wispslate notes");
        notSignedIn.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        notSignedIn.Reachability = ProjectMemorySourceReachability.NotSignedIn;
        viewModel.ResourceRows.Add(notSignedIn);

        var notFound = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "no-such-project", "Old project");
        notFound.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        notFound.Reachability = ProjectMemorySourceReachability.NotFound;
        viewModel.ResourceRows.Add(notFound);

        // Taller than the dialog opens, the same reason _ProjectEditorWithResources already gives: three rows'
        // worth of picker plus hint sit well below the fold of a default-sized editor.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    /// <summary>
    /// A single Instructions row with "Send along" ticked (AC-486): its own scene rather than a third row folded
    /// into <see cref="_ProjectEditorWithResources"/> above — see that method's own remarks on why combining the two
    /// pushed a hint <em>that</em> scene already had to prove visible below this dialog's fold. One row is enough to
    /// show what matters here: the checkbox sits beside "Tell sessions" without crowding it, and the hint underneath
    /// explaining what ticking it does is actually on screen, not merely present in the tree.
    /// </summary>
    private static ProjectDialog _ProjectEditorWithInstructionsSendAlong()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Instructions, "docs/handbook.md", "House conventions")
        {
            SendsContent = true,
        });

        // 1500 rather than a value nearer this scene's own resting height: DialogScreenClamp clamps down to 90% of
        // the (headless) screen regardless, and _ProjectEditorWithResources found that ceiling comfortably fits
        // every section above the resource list plus two full rows — a single row's own checkbox and hint fit with
        // room to spare. A smaller value here was tried first and clipped this scene's own hint text mid-sentence,
        // exactly what this scene exists to rule out.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    // Renders the Verify-runners dialog with the add/edit form open and pre-filled, so the labelled fields and the
    // form's own buttons are verifiable headless (the default scene shows the resting list state).
    private static VerifyRunnersDialog _VerifyRunnersEditing()
    {
        var viewModel = new ViewModels.VerifyRunnersViewModel();
        viewModel.NewRunnerCommand.Execute(null);
        viewModel.FillCockpitExampleCommand.Execute(null);
        viewModel.EditWorkingDirectory = "/home/me/AI-Cockpit";

        return new VerifyRunnersDialog { DataContext = viewModel };
    }

    // Renders the Manage-profiles editor on a Claude profile (a provider with a TTY route of its own) with the
    // Default kind (AC-139) set to the given kind, so the segmented toggle's two live states — SDK highlighted,
    // TTY highlighted — are both verifiable headless.
    private static ManageProfilesDialog _ManageProfilesWithDefaultKind(SessionKind defaultKind)
    {
        // The bundled Claude provider plugin's config, not a bare ClaudeConfig: Fase 4 only understands a Claude
        // profile as one of these (a legacy ClaudeConfig is migrated to it on load through SessionProfileEntry, a
        // step this design-time scene bypasses) — using the legacy shape directly here left the editor unable to
        // resolve a provider option for it at all, silently falling back to Ollama and hiding the very state
        // (a provider with a TTY route) this scene exists to show.
        var profile = new SessionProfile("work", ClaudePluginProfile.Create("/home/raymond/.claude-work", null), Purpose: "Primary Claude profile")
        {
            DefaultKind = defaultKind == SessionKind.Sdk ? ProfileSessionKind.Sdk : ProfileSessionKind.Tty,
        };
        return _ManageProfilesEditing(profile);
    }

    // Renders the Manage-profiles editor on a local (Ollama) profile — a provider with no TTY route at all — so the
    // Default-kind toggle's third state (AC-139) is verifiable headless: the segmented control disappears entirely
    // in favour of a plain "SDK-only" label, rather than offering a choice that could never take effect.
    private static ManageProfilesDialog _ManageProfilesSdkOnlyDefaultKind() =>
        _ManageProfilesEditing(new SessionProfile("local", new OllamaConfig("http://localhost:11434", "Qwen2.5-Coder:7b", null), Purpose: "cheap local model"));

    private static ManageProfilesDialog _ManageProfilesEditing(SessionProfile profile)
    {
        var viewModel = new ViewModels.ManageProfilesDialogViewModel();
        var editable = new ViewModels.EditableProfileViewModel(profile, isLoggedIn: true);
        viewModel.Profiles.Clear();
        viewModel.Profiles.Add(editable);
        viewModel.SelectedProfile = editable;

        // Taller than the dialog opens, the way the project editor's memory-source scene is: the Default kind
        // section sits below the fold of a default-sized editor, and a scene that renders only the part above the
        // fold proves nothing about the part this change actually touched.
        return new ManageProfilesDialog { DataContext = viewModel, Height = 1500 };
    }

    // Renders the Options dialog with one of its tabs selected, so a tab other than the first one can be
    // verified without a display.
    private static OptionsDialog _OptionsOnTab(string header)
    {
        var dialog = new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() };
        var tabs = dialog.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("The Options dialog has no 'Tabs' TabControl to select on.");

        tabs.SelectedItem = tabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The Options dialog has no '{header}' tab.");

        return dialog;
    }

    // Renders the sessions workspace with a couple of plugin toolbar actions seeded (AC-91) so the quick-action
    // buttons next to the workspace gear are verifiable headless.
    private static MainWindow _ToolbarActions()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "docker", new Cockpit.Plugins.Abstractions.ToolbarAction("Docker settings", Material.Icons.MaterialIconKind.Docker, () => Task.CompletedTask)));
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "kubernetes", new Cockpit.Plugins.Abstractions.ToolbarAction("Kubernetes settings", Material.Icons.MaterialIconKind.Kubernetes, () => Task.CompletedTask)));

        return new MainWindow { DataContext = cockpit };
    }

    /// <summary>A screenshot-shaped PNG at whatever aspect ratio a preview scene needs, from the same stand-in drawing the selection surface's own scenes use.</summary>
    private static byte[] _StandInPng(int width, int height)
    {
        using var bitmap = StandInDesktop.Draw(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    // A pane as it comes back after a crash: materialised, nothing started, and carrying its restore offer. The
    // degraded variant is the one worth looking at hardest — it drops the Resume button and gains a second line of
    // reason, so the banner has to stay readable while its widest element disappears and its tallest appears.
    private static Window _RestorePane(int width, int height, bool degraded)
    {
        var pane = new Cockpit.Core.Workspaces.WorkspacePane("pane-restored", Cockpit.Core.Workspaces.PaneKind.AiSession)
        {
            ProfileId = "personal",
            WorkingDirectory = "/home/raymond/dev/webshop",
            Title = "personal - 3",
        };

        var plan = new Services.SessionRestorePlan(
            pane,
            Profile: null,
            degraded ? Services.SessionRestoreAvailability.Gone : Services.SessionRestoreAvailability.Known,
            degraded ? "Claude no longer has this conversation: No conversation found with session ID: 9f2c1b40." : string.Empty);

        // SessionView for the resumable case and TtyView for the degraded one, so the two scenes between them
        // render the banner out of both files rather than twice out of the same one.
        var viewModel = new SessionViewModel { Title = pane.Title, ActiveProfileLabel = "personal", RestoreOffer = plan };
        var ttyViewModel = new TtyViewModel { Title = pane.Title, ActiveProfileLabel = "personal", RestoreOffer = plan };

        return new Window
        {
            Width = width,
            Height = height,
            Content = degraded
                ? new TtyView { DataContext = ttyViewModel }
                : new SessionView { DataContext = viewModel },
        };
    }

    // A live-looking transcript with a Task tool call whose sub-agent is (or just was) active — its own text,
    // tool call and result — nested under the Task row (AC-146), either at rest (collapsed, the default) or
    // expanded so the nested activity actually shows.
    /// <summary>
    /// AC-563: the header in the states its two hovers read differently. The provider chip is expected to show
    /// nothing at all once opened — the tools card is gone, and an absence is exactly what a passing test suite
    /// also looks like, so it gets a render of its own. The activity column carries the MCP servers instead:
    /// named, unknown (criterion 6 — never an empty list), and with an agent's statusline in the column
    /// (criterion 8 — the list must not leave with the words it replaced).
    /// </summary>
    private static SessionView _McpHeader(string? statusline = null, IReadOnlySet<string>? servers = null)
    {
        var viewModel = new SessionViewModel { Statusline = statusline ?? string.Empty, McpServerSelection = servers };
        // Off the selection, never typed out beside it — a scene that staged its own count would be free to stage
        // one the hover disagrees with, which is the very thing these renders exist to rule out.
        viewModel.Status = viewModel.ConnectedStatusLine;

        return new SessionView { DataContext = viewModel };
    }

    /// <summary>
    /// AC-562: the sliders flyout with the reading level in it, in both states criterion 3 separates — a
    /// provider that declares live controls, and one that declares none, where the button used to disappear
    /// and take the reading level with it.
    /// </summary>
    private static SessionView _SessionSettingsFlyout(bool withLiveControls)
    {
        var viewModel = new SessionViewModel();
        if (!withLiveControls)
        {
            viewModel.LiveControls.Clear();
        }

        return new SessionView { DataContext = viewModel };
    }

    private static SessionView _LinkTranscript()
    {
        var viewModel = new SessionViewModel { Title = "personal - webshop" };

        viewModel.Apply(new AssistantTextDelta
        {
            SessionId = "s1",
            BlockIndex = 0,
            Text = """
                   PR created: https://github.com/raymondkrahwinkel/AI-Cockpit/pull/365

                   Pushed and PR'd: **[#365](https://github.com/raymondkrahwinkel/AI-Cockpit/pull/365)**

                   Notes are in the *[release page](https://github.com/raymondkrahwinkel/AI-Cockpit/releases)*
                   (https://github.com/raymondkrahwinkel/AI-Cockpit/wiki), and `curl https://api.github.com/rate_limit`
                   stays plain text.
                   """,
        });

        return new SessionView { DataContext = viewModel };
    }

    private static SessionView _SubAgentSession(bool expanded)
    {
        var viewModel = new SessionViewModel { Title = "personal - webshop" };

        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Let me check the failing test." });
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_task1", ToolName = "Task", InputJson = """{"description":"Investigate the flaky checkout test","prompt":"Find why CheckoutTests.Total_WithDiscount flakes"}""" });
        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Reading the test and the code it exercises.", ParentToolUseId = "toolu_task1" });
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_sub1", ToolName = "Read", InputJson = """{"file_path":"tests/CheckoutTests.cs"}""", ParentToolUseId = "toolu_task1" });
        viewModel.Apply(new ToolResult { SessionId = "s1", ToolUseId = "toolu_sub1", Content = "public void Total_WithDiscount() { ... }", IsError = false, ParentToolUseId = "toolu_task1" });
        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 1, Text = "Found it: the discount rounds before tax is applied on some locales.", ParentToolUseId = "toolu_task1" });

        var anchor = viewModel.Transcript.Single(row => row.ToolUseId == "toolu_task1");
        anchor.IsSubAgentExpanded = expanded;

        return new SessionView { DataContext = viewModel };
    }

    // Renders the MCP-servers dialog in the state that had no way of being looked at (AC-427): an OAuth server, so
    // the sign-in block and both OAuth fields are showing, custom headers so the form overflows, and the notice
    // about a hidden server, which is the longest thing the footer is ever asked to hold. That combination is what
    // pushed Cancel and Save off the window, and the whole of it is in one frame here.
    private static McpServersDialog _McpServers()
    {
        var viewModel = new McpServersViewModel
        {
            StatusMessage = "Hidden here because the cockpit already runs a server by that name: filesystem, fetch. "
                            + "Saving removes them — rename yours first if you meant to keep it.",
        };

        var server = viewModel.Servers[0];
        server.Transport = McpTransport.Http;
        server.Url = "https://mcp.example.com/mcp";
        server.Auth = McpServerAuth.OAuth;
        server.OAuthAuthority = "https://login.example.com";
        server.Headers.Add(new McpHeaderRowViewModel("X-Api-Key", "a-value"));
        server.Headers.Add(new McpHeaderRowViewModel("X-Tenant", "cockpit"));

        return new McpServersDialog { DataContext = viewModel };
    }

    // The state Sign in used to be dead in (AC-499): a row the operator just added and filled in, never saved.
    // Before this ticket the button stayed disabled here with "Save this server first"; now it is offered, and
    // clicking it saves the whole dialog before it authorizes.
    private static McpServersDialog _McpServersSignInUnsaved()
    {
        var viewModel = new McpServersViewModel();
        viewModel.Servers.Clear();
        var server = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "depot", Transport = McpTransport.Http, Url = "https://depot.example/mcp", Auth = McpServerAuth.OAuth },
            NoOpOAuthCoordinator.Instance,
            isPersisted: false);
        viewModel.Servers.Add(server);
        viewModel.SelectedServer = server;

        // Taller than the dialog opens, the way the profiles/project-editor scenes already are: the sign-in block
        // sits below the fold of a default-sized dialog, and DialogScreenClamp still caps the actual render at 90%
        // of the headless screen regardless of this number — it only pushes the block as high as that ceiling allows.
        return new McpServersDialog { DataContext = viewModel, Height = 1500 };
    }

    // A row that is not valid yet (AC-499) — a name but no URL — so Sign in stays refused, and
    // SignInUnavailableReason now names what is missing instead of asking for a save that is no longer the gate.
    private static McpServersDialog _McpServersSignInInvalid()
    {
        var viewModel = new McpServersViewModel();
        viewModel.Servers.Clear();
        var server = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "vault", Transport = McpTransport.Http, Auth = McpServerAuth.OAuth },
            NoOpOAuthCoordinator.Instance,
            isPersisted: false);
        viewModel.Servers.Add(server);
        viewModel.SelectedServer = server;

        return new McpServersDialog { DataContext = viewModel, Height = 1500 };
    }

    // Mid-flight (AC-499): IsAuthBusy now covers the save this row's own sign-in does first, not just the
    // coordinator round trip, so both buttons stay disabled and "Working…" shows for the whole of it.
    private static McpServersDialog _McpServersSignInBusy()
    {
        var viewModel = new McpServersViewModel();
        viewModel.Servers.Clear();
        var server = new EditableMcpServerViewModel(
            new McpServerConfig { Name = "depot", Transport = McpTransport.Http, Url = "https://depot.example/mcp", Auth = McpServerAuth.OAuth },
            NoOpOAuthCoordinator.Instance)
        {
            IsAuthBusy = true,
        };
        viewModel.Servers.Add(server);
        viewModel.SelectedServer = server;

        return new McpServersDialog { DataContext = viewModel, Height = 1500 };
    }

    /// <summary>A coordinator that never does anything, for the three sign-in scenes above — they only need Sign
    /// in's own gate to see a non-null coordinator, never an actual call.</summary>
    private sealed class NoOpOAuthCoordinator : IMcpOAuthCoordinator
    {
        public static readonly NoOpOAuthCoordinator Instance = new();

        public Task<McpOAuthAccess> AcquireAsync(McpServerConfig server, bool interactive, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.NotRequired);

        public Task<McpOAuthAccess> AcquireForSessionAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.NotRequired);

        public Task<McpOAuthAccess> RenewRejectedAsync(McpServerConfig server, string rejectedAccessToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpOAuthAccess.NotRequired);

        public Task<McpAuthState> GetStateAsync(McpServerConfig server, CancellationToken cancellationToken = default) =>
            Task.FromResult(McpAuthState.AuthorizationRequired);

        public Task SignOutAsync(McpServerConfig server, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Renders the full window with a plugin-update count seeded (AC-76) so the sidebar "Plugin store" button's
    // accent update badge is verifiable headless.
    private static MainWindow _PluginUpdateBadge()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.Plugins.SetUpdateBadgeCount(3);

        return new MainWindow { DataContext = cockpit };
    }

    // Renders the plugin store (#62) with a sample catalogue seeded straight into the manager's collections
    // (no network browse — the dialog only loads on the real app's open), so its layout — the
    // categories | plugins | details columns, the Installed/Updates group pinned to the sidebar foot, the
    // list rows and their install-state — can be verified headless.
    private static PluginStoreDialog _PluginStore() => new() { DataContext = _PluginStoreViewModel() };

    // The store while it is working (AC-420): a single install, whose bar has no fraction to draw and runs
    // indeterminate, and a batch update, whose bar is fed by the same counter the status line is. NeedsRestart
    // is on in both because that is the state that was reported — "Update all" raises it after the first plugin
    // of the batch, so the footer offers a restart while the rest are still downloading.
    //
    // What this scene does *not* show is the restart gate. It is built on the design-time view model, which has
    // no restart service, so its restart button is dead here whatever IsBusy says. The gate is held by
    // PluginStoreBusyGateTests, which builds a manager that can actually restart.
    private static PluginStoreDialog _PluginStoreBusy(double? percent, string status)
    {
        var viewModel = _PluginStoreViewModel();
        viewModel.Manager.StatusMessage = status;
        viewModel.Manager.BusyProgressIndeterminate = percent is null;
        viewModel.Manager.BusyProgressValue = percent ?? 0;
        viewModel.Manager.NeedsRestart = true;
        viewModel.Manager.IsBusy = true;

        return new PluginStoreDialog { DataContext = viewModel };
    }

    private static PluginStoreDialogViewModel _PluginStoreViewModel()
    {
        var manager = new PluginManagerViewModel();
        manager.Stores.Add(PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"));
        foreach (var row in _SampleStorePlugins())
        {
            manager.AvailablePlugins.Add(row);
        }

        return new PluginStoreDialogViewModel(manager)
        {
            SelectedPlugin = manager.AvailablePlugins.FirstOrDefault(),
        };
    }

    // Renders the Manage-stores dialog (#62, AC-7) with a few sample stores seeded straight into the manager's
    // StoreInfos — a private remote one (a token, so the lock badge shows) with a logo, a public remote falling
    // back to a URL-derived name and default glyph, and a local-folder one (the folder badge) — so its layout and
    // the icon/name/count/badge rows can be verified headless.
    private static ManageStoresDialog _ManageStores()
    {
        var manager = new PluginManagerViewModel();
        // A real logo image (the app icon stands in for a store's own), so the screenshot shows the fetched-image
        // path rather than only the emoji fallback.
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Remote("https://github.com/aicockpit/plugins", "sample-token"))
        {
            Name = "AI-Cockpit Plugins", PluginCount = 13, IsReachable = true, IsBrowsed = true,
            Logo = _LoadAssetBitmap("avares://Cockpit.App/Assets/AppIcon.png"),
        });
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Remote("https://raw.githubusercontent.com/raymond/cockpit-extras/main/index.json"))
        {
            PluginCount = 4, IsReachable = true, IsBrowsed = true,
        });
        manager.StoreInfos.Add(new PluginStoreInfo(PluginStoreConfig.Local("/home/you/my-plugins"))
        {
            PluginCount = 2, IsReachable = true, IsBrowsed = true,
        });

        return new ManageStoresDialog { DataContext = manager };
    }

    // The clone dialog after a clone was refused (AC-90): the URL and destination the operator typed are still
    // there, and the failure sits in the form under them. That line is the reason this scene picks the failed
    // state — it is the one part of the dialog that only exists after something went wrong.
    private static CloneFromGitUrlDialog _CloneFromGitUrl() => new()
    {
        DataContext = new ViewModels.CloneFromGitUrlDialogViewModel
        {
            Url = "https://github.com/aicockpit/cockpit-extras.git",
            TargetFolder = "/home/you/code/cockpit-extras",
            ErrorMessage = "Authentication failed. The repository is private — check the credentials git uses for this host.",
        },
    };

    // The palette with commands in it, taken from the catalogue the real one is built from rather than written
    // out here — a made-up row renders a shortcut this app does not have, on a picture that looks like the app.
    // The catalogue also supplies what the scene is for: it binds some actions and deliberately leaves others
    // unbound, and an unbound one shows a blank where the gesture goes, which a list of only-bound rows would not.
    private static CommandPaletteDialog _CommandPalette()
    {
        var commands = Cockpit.Core.Shortcuts.ShortcutCatalog.All
            .Select(shortcut => new ViewModels.PaletteCommand(shortcut.Label, shortcut.DefaultGesture, () => { }))
            .ToList();

        return new CommandPaletteDialog { DataContext = new ViewModels.CommandPaletteDialogViewModel(commands) };
    }

    // What a backup offers to put back (#70), with one plugin this cockpit already has and one it has never had —
    // the two rows read differently, and a manifest carrying only one kind would show only one of them.
    private static RestoreSelectionDialog _RestoreSelection()
    {
        var manifest = new Cockpit.Core.Backup.BackupManifest(
            Cockpit.Core.Backup.BackupManifest.CurrentSchema,
            "1.4.0",
            DateTimeOffset.UtcNow.AddDays(-9),
            IncludesCredentials: false,
            RemovedSecrets: ["Anthropic API key"],
            ProfileConfigDirectories: new Dictionary<string, string>(),
            Plugins: new Dictionary<string, string>
            {
                ["git-status"] = "1.4.0",
                ["transcript-search"] = "1.2.0",
            });

        return new RestoreSelectionDialog { DataContext = new ViewModels.RestoreSelectionViewModel(manifest, ["git-status"]) };
    }

    // The push-to-talk pill in one of its rows. The bars are given rising levels rather than the resting height
    // the view model starts them at, because a waveform flat at its minimum is the picture this window's
    // "unavailable" row exists to stop being shown.
    private static VoiceOverlayWindow _VoiceOverlay(ViewModels.VoiceOverlayState state, string? status = null, double? progress = null)
    {
        var viewModel = new ViewModels.VoiceOverlayViewModel
        {
            State = state,
            StatusText = status ?? string.Empty,
            Progress = progress,
        };

        for (var bar = 0; bar < viewModel.Bars.Count; bar++)
        {
            viewModel.Bars[bar].Height = 4 + (12 * Math.Abs(Math.Sin(bar * 0.7)));
        }

        return new VoiceOverlayWindow { DataContext = viewModel };
    }

    /// <summary>
    /// Every assistant chip state stacked at the width the sidebar really gives it, so they can be judged against
    /// each other rather than one at a time in a window with room to spare.
    /// </summary>
    /// <remarks>
    /// The width is the point. Rendered at 340px every state looks fine; at the sidebar's own ~164px the label
    /// wraps mid-word, the key hint pushes the text out, and the states stop lining up — which is how two rounds
    /// of visual defects got past a full set of green renders and reached the operator instead.
    /// </remarks>
    private static Window _AssistantIndicatorGallery()
    {
        const double SidebarContentWidth = 164;

        var states = new (Cockpit.Core.Assistant.AssistantActivity Activity, string? Reason)[]
        {
            (Cockpit.Core.Assistant.AssistantActivity.Ready, null),
            (Cockpit.Core.Assistant.AssistantActivity.Listening, null),
            (Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously, null),
            (Cockpit.Core.Assistant.AssistantActivity.Thinking, null),
            (Cockpit.Core.Assistant.AssistantActivity.Speaking, null),
            (Cockpit.Core.Assistant.AssistantActivity.Dictating, null),
            (Cockpit.Core.Assistant.AssistantActivity.Unavailable, "No model on this machine"),
        };

        var column = new StackPanel { Spacing = 10, Margin = new Avalonia.Thickness(12) };
        foreach (var (activity, reason) in states)
        {
            column.Children.Add(new Views.AssistantIndicator
            {
                Width = SidebarContentWidth,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                DataContext = new ViewModels.AssistantIndicatorViewModel
                {
                    Activity = activity,
                    UnavailableReason = reason,
                    ProfileLabel = "default (Claude CLI)",
                    ListeningMode = activity == Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously
                        ? Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn
                        : Cockpit.Core.Assistant.AssistantListeningMode.Off,
                },
            });
        }

        return new Window
        {
            Width = 220,
            Height = 720,
            Background = Application.Current?.FindResource("CockpitSecondaryBgBrush") as Avalonia.Media.IBrush,
            Content = new ScrollViewer { Content = column },
        };
    }

    // AC-543 criterion 11: the whole window, with the assistant chip where it actually sits. The chip is fed by
    // AssistantIndicatorCoordinator at runtime; here it is set directly, for the reason the component takes its
    // state rather than fetching it — a scene that had to stand up a session host to draw a sidebar would be
    // testing the host.
    private static Window _SidebarWithAssistant(bool collapsed, bool featureEnabled = true)
    {
        var cockpit = new ViewModels.CockpitViewModel
        {
            GlobalSingleSessionLayout = true,
            SidebarCollapsed = collapsed,
            AssistantIndicator = new ViewModels.AssistantIndicatorViewModel
            {
                // Listening continuously rather than idle: it is the state that carries the most on screen (colour,
                // words, and the mode dot), so a render of it fails visibly where "Ready" would look plausible
                // whatever went wrong.
                Activity = Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously,
                ListeningMode = Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn,
                IsCollapsed = collapsed,
                IsFeatureEnabled = featureEnabled,
            },
        };

        return new MainWindow { DataContext = cockpit };
    }

    // AC-543 (strand 3): builds the assistant indicator on a view model set directly to the state a scene name
    // asks for — the indicator is fed its state rather than owning it (AssistantIndicatorViewModel's own remarks
    // on why it does not bind to AssistantSessionHost), so a scene only ever has to set properties, never wire up
    // a fake host.
    private static Window _AssistantIndicator(
        Cockpit.Core.Assistant.AssistantActivity activity,
        string? unavailableReason = null,
        Cockpit.Core.Assistant.AssistantListeningMode listeningMode = Cockpit.Core.Assistant.AssistantListeningMode.Off,
        bool collapsed = false,
        bool alwaysOnConfirmationPending = false,
        string? profileLabel = null)
    {
        var viewModel = new ViewModels.AssistantIndicatorViewModel
        {
            Activity = activity,
            UnavailableReason = unavailableReason,
            ListeningMode = listeningMode,
            IsCollapsed = collapsed,
            IsAlwaysOnConfirmationPending = alwaysOnConfirmationPending,
            ProfileLabel = profileLabel,
        };

        return new Window
        {
            Width = collapsed ? 120 : 340,
            Height = collapsed ? 120 : (alwaysOnConfirmationPending ? 320 : 220),
            Content = new Views.AssistantIndicator { DataContext = viewModel },
        };
    }

    // AC-543 (strand 4): the chat pop-out on a minimal fake of the host it reads from — a settled-on integration
    // seam (AssistantChatViewModel's own remarks) rather than a Screenshotter invention: AssistantSessionHost
    // (src/Cockpit.App/Services/AssistantSessionHost.cs) already carries this exact shape but is sealed with a
    // live CockpitViewModel among its constructor dependencies, far too heavy to stand up for a render. Reuses
    // SessionViewModel's own parameterless constructor sample data for "withConversation" (the same rows the
    // "session" scene and the Avalonia previewer render) rather than inventing a second sample transcript —
    // that data already includes one expanded and one collapsed tool call, so the collapsed one proves criterion
    // 11's "an inklapbare tool-call" without a bespoke fixture.
    private static AssistantChatWindow _AssistantChat(bool withConversation, bool speakReplies = true)
    {
        var host = new _FakeAssistantSessionHost
        {
            Session = withConversation ? new ViewModels.SessionViewModel() : null,
            Activity = Cockpit.Core.Assistant.AssistantActivity.Ready,
        };

        var viewModel = new ViewModels.AssistantChatViewModel(host, new _FakeAssistantSettingsStore(speakReplies), new _NullVoicePlaybackQueue());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // Bare-minimum IAssistantSessionHost: nothing mutates Session/Activity/UnavailableReason after a scene
    // constructs one, so PropertyChanged never actually has to fire — a single captured frame never sees a
    // change. EnsureStartedAsync/SendAsync just hand back what is already set rather than simulating the real
    // host's lazy-start/queueing behaviour, which no scene exercises either.
    private sealed class _FakeAssistantSessionHost : ViewModels.IAssistantSessionHost
    {
        public ViewModels.SessionViewModel? Session { get; init; }

        public Cockpit.Core.Assistant.AssistantActivity Activity { get; init; }

        public string? UnavailableReason { get; init; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public Task<ViewModels.SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

        public Task SendAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;

        // Same reason as ApplySettingsAsync below: a scene is staged and rendered, and speaking is not something a
        // still frame can show either way.
        public void SetSpeakReplies(bool speak) { }

        // A scene is staged into the state its name describes and then rendered; re-reading settings would only
        // move it off that state.
        public Task ApplySettingsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void ReportHoldListening(bool listening)
        {
        }
    }

    // Bare-minimum IAssistantSettingsStore: hands back a fixed AssistantSettings with SpeakReplies pre-set to
    // what the scene asks for, and discards anything a scene's own toggle interaction would try to save — there
    // is no cockpit.json to round-trip through in a headless render.
    private sealed class _FakeAssistantSettingsStore(bool speakReplies) : Cockpit.Core.Abstractions.Assistant.IAssistantSettingsStore
    {
        public Task<Cockpit.Core.Assistant.AssistantSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Cockpit.Core.Assistant.AssistantSettings { IsEnabled = true, SpeakReplies = speakReplies });

        public Task SaveAsync(Cockpit.Core.Assistant.AssistantSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Bare-minimum IVoicePlaybackQueue: a captured frame never plays audio, so every member is a no-op/empty
    // answer — this exists only so AssistantChatViewModel's constructor has something to call StopAll on.
    private sealed class _NullVoicePlaybackQueue : Cockpit.Core.Abstractions.Voice.IVoicePlaybackQueue
    {
        public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language)
        {
        }

        public void Enqueue(IReadOnlyList<Cockpit.Core.Voice.SpeechSegment> segments, int speakerId)
        {
        }

        public void NotifyPreparing()
        {
        }

        public event EventHandler<bool>? PlaybackActiveChanged { add { } remove { } }

        public event EventHandler? SpeakingStarted { add { } remove { } }

        public void StopAll()
        {
        }

        public int Generation => 0;
    }

    private static Bitmap? _LoadAssetBitmap(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<StorePluginRowViewModel> _SampleStorePlugins()
    {
        static StorePluginRowViewModel Row(
            string id, string name, string description, string category, string version, string icon,
            bool featured, bool installed, bool hasSettings = false, bool homepage = false, bool repository = false)
        {
            var versions = new[] { new PluginStoreVersion(version, $"plugins/{id}.zip", null, null, null, null) };
            var entry = new PluginStoreEntry(
                id, name, description, "Cockpit", version, versions, category, icon,
                homepage ? $"https://aicockpit.dev/{id}" : null,
                repository ? $"https://github.com/aicockpit/{id}" : null,
                featured, "2026-07-10");
            // installedVersion == latest ⇒ shown as installed and up to date (a green "Installed" pill),
            // null ⇒ available (the accent "Install" call-to-action).
            return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"),
                installed ? version : null, isEnabled: installed, hasSettings: hasSettings);
        }

        return
        [
            Row("github-issues", "GitHub Issues", "Browse open GitHub issues across your repos (via the gh CLI) or one repo in a dedicated panel.", "Issue trackers", "1.8.0", "🐙", featured: true, installed: false, homepage: true, repository: true),
            Row("workflows", "Workflows", "A visual editor for cockpit workflows, and an engine that runs them: drop steps onto a canvas and wire them up.", "Automation", "0.22.0", "🔀", featured: true, installed: true, homepage: true, repository: true),
            Row("claude-bundled", "Claude Code", "Claude as a provider plugin (Fase 4). Runs the real interactive Claude TUI in a session panel.", "AI providers", "0.3.1", "🌸", featured: false, installed: true, homepage: true),
            Row("clock", "Clock", "The time and date, for a Dashboard workspace. Ships with the cockpit, so it is always there.", "Widgets", "1.0.0", "🕐", featured: false, installed: true),
            Row("system-monitor", "System Monitor", "CPU, memory and disk usage for a Dashboard workspace. You pick which stats show.", "Widgets", "1.0.0", "🖥", featured: false, installed: true),
            Row("git-status", "Git status", "A git indicator in every session — a coloured dot and the branch, so you always know the repo state.", "Productivity", "1.4.0", "🌱", featured: false, installed: true, hasSettings: true, repository: true),
            Row("transcript-search", "Claude Transcript Search", "Search everything you and the agent ever wrote in a Claude CLI session.", "Productivity", "1.2.0", "🔍", featured: false, installed: true, repository: true),
            Row("codex-provider", "CLI Agent Provider (Codex)", "Adds Codex CLI as a selectable session provider, driven as a subprocess per session.", "AI providers", "0.2.0", "🧩", featured: false, installed: false, homepage: true, repository: true),
            Row("gemini-openai", "Gemini / OpenAI Provider", "Adds Gemini and OpenAI models as selectable session providers, keyed per profile.", "AI providers", "0.4.0", "✨", featured: false, installed: true, hasSettings: true, homepage: true, repository: true),
        ];
    }

    private static AppBuilder BuildHeadlessAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            // The app's own font, for the same reason it takes the app's own fonts options: a render is only
            // worth looking at if it is of this program. Without it the harness measured text in whatever the
            // machine happened to offer, which is also how the same window came out with a scroll bar here and
            // none on CI.
            .WithInterFont()
            .With(Program.CockpitFontOptions())
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}

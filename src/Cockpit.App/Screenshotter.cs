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
using Cockpit.Core.Abstractions.Mentions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Mcp;
using Cockpit.Core.Plugins;
using Cockpit.Core.Profiles;
using Cockpit.Core.Projects;
using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.App;

// Lets an external caller verify the UI layout without a display attached (Iron Law #9: automated visual verification).
internal static class Screenshotter
{
    private const int DefaultWindowWidth = 1100;
    private const int DefaultWindowHeight = 760;

    public const string Argument = "--screenshot";

    // A render that has not finished by now is stuck, not slow — and a stuck render used to hold the process
    // until somebody killed it, which from the outside is indistinguishable from a scene that takes a while (AC-1235).
    private static readonly TimeSpan RenderDeadline = TimeSpan.FromSeconds(120);

    public static bool IsRequested(string[] args) => args.Contains(Argument);

    // The whole command line this route answers, parsed here rather than in `Program` so that nothing between the
    // switch and the render touches the operator's state — see the call site for why that matters (AC-1235).
    // Returns the process exit code: every failure says why on stderr, and success names the file it wrote.
    public static int RunFromCommandLine(string[] args)
    {
        var index = Array.IndexOf(args, Argument);
        if (index + 1 >= args.Length)
        {
            Console.Error.WriteLine($"{Argument} requires an output PNG path argument.");

            return 1;
        }

        var outputPngPath = args[index + 1];

        // "--snapshot <path>" also dumps the laid-out visual tree as text (AC-86 verify loop), and
        // "--snapshot-target <x:Name>" scopes that dump to one control's subtree.
        var scene = _Value(args, "--scene");
        var snapshotPath = _Value(args, "--snapshot");
        var snapshotTarget = _Value(args, "--snapshot-target");

        var width = DefaultWindowWidth;
        var height = DefaultWindowHeight;
        if (_Value(args, "--size") is { } size)
        {
            if (size.Split('x') is not [var rawWidth, var rawHeight] ||
                !int.TryParse(rawWidth, out width) || !int.TryParse(rawHeight, out height))
            {
                Console.Error.WriteLine($"--size must be WxH in whole pixels; it is \"{size}\".");

                return 1;
            }
        }

        using var deadline = new Timer(
            _ =>
            {
                Console.Error.WriteLine(
                    $"Screenshot of scene \"{scene ?? "main window"}\" did not finish within {RenderDeadline.TotalSeconds:0} seconds; giving up.");
                Console.Error.Flush();
                Environment.Exit(1);
            },
            state: null,
            RenderDeadline,
            Timeout.InfiniteTimeSpan);

        try
        {
            Run(outputPngPath, width, height, scene, snapshotPath, snapshotTarget);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Screenshot failed: {exception.Message}");

            return 1;
        }

        // The whole point of this route is a file an agent can look at, so the run only reports success once it has
        // one — an encoder that writes nothing used to leave exit 0 and an empty directory behind (AC-1235). Naming
        // the resolved path is what makes a relative one that landed elsewhere obvious rather than missing.
        var written = new FileInfo(Path.GetFullPath(outputPngPath));
        if (!written.Exists || written.Length == 0)
        {
            Console.Error.WriteLine($"Screenshot reported success but wrote no image to \"{written.FullName}\".");

            return 1;
        }

        Console.WriteLine($"Wrote {written.FullName} ({written.Length} bytes).");

        return 0;
    }

    private static string? _Value(string[] args, string name) =>
        Array.IndexOf(args, name) is var index && index >= 0 && index + 1 < args.Length ? args[index + 1] : null;

    public static void Run(string outputPngPath, int width = DefaultWindowWidth, int height = DefaultWindowHeight, string? scene = null, string? snapshotPath = null, string? snapshotTarget = null)
    {
        if (!Path.GetExtension(outputPngPath).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Screenshot output path must have a .png extension.", nameof(outputPngPath));
        }

        BuildHeadlessAvaloniaApp().SetupWithoutStarting();

        var window = ShowScene(scene, width, height);

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless renderer produced no frame to capture.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPngPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        frame.Save(outputPngPath, PngBitmapEncoderOptions.Default);

        if (!string.IsNullOrEmpty(snapshotPath))
        {
            _WriteSnapshot(window, snapshotPath, snapshotTarget);
        }

        window.Close();
    }

    // The window each scene name asks for. A table rather than a switch so the set of names can be read off it:
    // anything that has to cover every screen — the theme baseline (AC-338) above all — would otherwise be
    // working from a hand-written list, and a hand-written list is blind to exactly the scene nobody remembered.
    private static readonly Dictionary<string, Func<int, int, Window>> Scenes = new(StringComparer.Ordinal)
    {
        ["about"] = (_, _) => new AboutDialog { DataContext = ViewModels.AboutInfo.FromAssembly(typeof(Screenshotter).Assembly) },
        // AC-512: the in-app glossary — the guide's own depth stays on the website, this is what a fresh install
        // can still read without a browser.
        ["glossary"] = (_, _) => new GlossaryDialog(),
        // AC-512: the sidebar's Help flyout, opened the way session-settings-flyout already proves headless
        // rendering can — after the window is up, via the Hovers table below.
        ["help-menu"] = (_, _) => new MainWindow { DataContext = new ViewModels.CockpitViewModel() },
        // AC-937: the sidebar's "Plugins ›" flyout, opened the way help-menu already proves headless rendering can.
        // Autopilot and Open PRs pinned (the shipped default), YouTrack and Workflows collapsed — one of the
        // collapsed entries carries a badge, so the flyout also shows what a live counter looks like behind it.
        ["plugins-menu"] = (_, _) => _PluginsMenuScene(),
        // AC-1033: the knowledge base in the five states that decide whether it works. A plugin's own branch
        // is not staged here — it would need a plugin assembly this build has not loaded; HelpWindowTests
        // covers it instead, in both configurations.
        ["help"] = (_, _) => _Help(null),
        ["help-article"] = (_, _) => _Help(new Core.Help.HelpAddress("welcome")),
        ["help-deep-link"] = (_, _) => _Help(
            new Core.Help.HelpAddress("core-concepts", "profile"), "a “?” beside a session's profile"),
        ["help-search"] = (_, _) => _HelpSearching("plugin"),
        ["help-broken-link"] = (_, _) => _Help(new Core.Help.HelpAddress("slack", "interactivity")),
        ["single-instance"] = (_, _) => new SingleInstanceNoticeDialog(),
        ["options"] = (_, _) => new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() },
        ["shortcuts"] = (_, _) => _OptionsOnTab("Shortcuts"),
        ["debug"] = (_, _) => _OptionsOnTab("Debug"),
        // AC-445: the Layout section (single session / stack vertically / focus + rail) lives on the Sessions
        // tab, not the Notifications tab "options" opens on.
        ["session-layout"] = (_, _) => _OptionsOnTab("Sessions"),
        // AC-445: the workspace ⚙'s own Layout flyout, opened the way session-settings-flyout already proves
        // headless rendering can. Needs a session so `ShowSessionGrid` shows the toolbar the ⚙ lives in.
        ["workspace-layout-flyout"] = (width, height) => _MainWindowWithOneSession(width, height),
        // AC-1000: Voice and Assistant are now separate top-level categories rather than Carousel sub-pages of one
        // Voice tab — own scenes rather than reusing "options", since neither category renders on the category that
        // scene opens on (Notifications) and a layout change to a page nothing captures is a layout change nobody
        ["voice-transcribe"] = (_, _) => _OptionsOnTab("Voice"),
        ["voice-assistant"] = (_, _) => _OptionsAssistantPage(),
        // AC-1000: the consent-bypass state (one recognised source, one orphaned #K11 key) that used to render on
        // the old "voice-assistant" scene now lives on Security — its own scene rather than folding it into a plain
        // "security" scene, since nothing else needs that seeded ConsentBypassSources state.
        ["security-consent-bypass"] = (_, _) => _OptionsSecurityConsentBypassPage(),
        ["profiles"] = (_, _) => new ManageProfilesDialog { DataContext = new ViewModels.ManageProfilesDialogViewModel(), Height = 900 },
        // AC-1019: Options → Profiles at a resting height much shorter than the selected profile's detail form, so
        // the list column (with Add/Remove) and the detail column can be seen scrolling independently rather than
        // sharing one page-wide ScrollViewer.
        ["options-profiles"] = (_, _) => _OptionsProfilesPage(),
        // Its own scene rather than a state of "profiles": it is a different window with a different, shorter set of
        // blocks, and the one control this ticket moved — the restart, which only shows with a living assistant behind
        // it — renders nowhere else.
        ["assistant-profile"] = (_, _) => new AssistantProfileDialog
        {
            DataContext = new ViewModels.AssistantProfileDialogViewModel(
                new _FakeAssistantSessionHost(), new _FakeClaudeProviderRegistry()),
            Height = 1220,
        },
        // The Default kind editor (AC-139) in each of its three states: a Claude profile (has a TTY route) with the
        // toggle pre-set to TTY, the same profile with it pre-set to SDK, and a local-provider profile (no TTY route at
        // all) where the toggle disappears in favour of a plain "SDK-only" label.
        ["profiles-default-kind-tty"] = (_, _) => _ManageProfilesWithDefaultKind(SessionKind.Tty),
        ["profiles-default-kind-sdk"] = (_, _) => _ManageProfilesWithDefaultKind(SessionKind.Sdk),
        ["profiles-default-kind-sdk-only"] = (_, _) => _ManageProfilesSdkOnlyDefaultKind(),
        // AC-1102: SESSION DEFAULTS had no scene at all, so the one block where an option can read as set or unset
        // could not be looked at. Stages both states and both control shapes at once — see the builder's remarks.
        ["profiles-plugin-option-defaults"] = (_, _) => _ManageProfilesPluginOptionDefaults(),
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
        // AC-605: the three scope shapes/actions that scene above has no room left to show alongside its own two
        // rows — see _ProjectEditorWithResourceScopes's own remarks on why this is a separate scene rather than
        // more rows crammed into that one.
        ["project-editor-resource-scopes"] = (_, _) => _ProjectEditorWithResourceScopes(),
        // Name and Behaviour are shared (Name read-only, Behaviour editable — the badge's own two lock states),
        // Description/ Logo/MCP overlay/the worktree switch stay local, each carrying its own "● This machine" badge
        // rather than no badge at all, since the project as a whole has a claim (HasFieldOwnership) (AC-604).
        ["project-editor-ownership"] = (_, _) => _ProjectEditorWithOwnership(),
        // AC-620, IL#9: the confirmation screen before a local project's first publish — the design-time sample already
        // carries a portable resource, a machine-scope one and a filled connection/target picker, so this scene draws
        // the same populated state the design-time previewer does rather than an empty shell (AC-699).
        ["share-project-dialog"] = (_, _) => new ShareProjectDialog { DataContext = new ShareProjectDialogViewModel(), Height = 900 },
        // Stage populated and empty server families together to prove both row states within the dialog clamp
        // (AC-499).
        ["project-editor-memory-source-families"] = (_, _) => _ProjectEditorWithMemorySourceFamilies(),
        // Give each mutually exclusive picker state its own scene so sign-in and load failures cannot look like an
        // empty list (AC-502).
        ["memory-source-location-picker"] = (_, _) => _MemorySourceLocationPicker(),
        ["memory-source-location-picker-sign-in"] = (_, _) => _MemorySourceLocationPickerSignIn(),
        ["memory-source-location-picker-error"] = (_, _) => _MemorySourceLocationPickerError(),
        // Use a long, mixed-height list to prove the preselected current row scrolls into view correctly (AC-499).
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
        // AC-612: a row pointing at a likely secrets location — its own scene rather than a row folded into
        // project-editor-resources or project-editor-resource-scopes above, the same "no room left under the fold"
        // reasoning both of those scenes' own remarks already give, and see _ProjectEditorWithSecretPath's own remarks
        ["project-editor-secret-path"] = (_, _) => _ProjectEditorWithSecretPath(),
        ["projects"] = (_, _) => new ProjectsDialog { DataContext = ViewModels.ProjectsViewModel.DesignSample() },
        // AC-245: the "Shared via Depot — …" groups beside the local projects — one healthy group with two rows
        // (name/description/role pill, "Not set up yet" badge), one group carrying a source error instead.
        ["projects-shared"] = (_, _) => new ProjectsDialog { DataContext = ViewModels.ProjectsViewModel.DesignSampleWithSharedProjects() },
        // AC-246: the "Finish setting up…" bind step — bare, Profile required and Folder optional, matching the AC-242
        // mockup section 4.
        ["shared-project-binding"] = (_, _) => new SharedProjectBindingDialog { DataContext = new ViewModels.SharedProjectBindingDialogViewModel() },
        // AC-247, mockup section 6: the conflict window ProjectDialogViewModel.SaveAsync opens on a checksum
        // mismatch — the design-time instance's own two rows (one collision, one one-sided remote change) prove
        // both row shapes render and the warning note only shows for the collision.
        ["project-conflict"] = (_, _) => new ProjectDefinitionConflictDialog { DataContext = new ViewModels.ProjectDefinitionConflictViewModel() },
        // --size 900x900) used to get the built-in size back regardless — the block rendered, but entirely below the
        // fold, which is what actually happened here (measured: at the built-in size only the section's heading,
        // half-cut, was ever on screen) (AC-246).
        ["shared-project-binding-resource-rows"] = (width, height) =>
            new SharedProjectBindingDialog { DataContext = ViewModels.SharedProjectBindingDialogViewModel.DesignSampleWithResourceRows(), Width = width, Height = height },
        // AC-618: categories as the list's main grouping, in a non-alphabetical order ("Werk" before "Privé"), with
        // "Uncategorized" always last (and shown even though nothing is left in it here) and every card's origin
        // badge — "● This machine" and "◆ Depot — Work" — now that the old "On this machine" heading is gone.
        ["projects-categories"] = (_, _) => new ProjectsDialog { DataContext = ViewModels.ProjectsViewModel.DesignSampleWithCategories() },
        // AC-772 criterion 15: the Projects workspace, not the Manage-projects window, once per layout.
        ["projects-workspace-cards"] = (_, _) => _ProjectsWorkspace(Cockpit.Core.Projects.ProjectsLayoutMode.Cards),
        ["projects-workspace-list"] = (_, _) => _ProjectsWorkspace(Cockpit.Core.Projects.ProjectsLayoutMode.List),
        // Rendered although the segment is not offered yet (ProjectsDisplaySettings.ContinueLayoutAvailable) — the
        // layout is built, and this is what will show whether it is worth offering.
        ["projects-workspace-continue"] = (_, _) => _ProjectsWorkspace(Cockpit.Core.Projects.ProjectsLayoutMode.Continue),
        ["plugin-store"] = (_, _) => _PluginStore(),
        // AC-553: the eleven bundled plugins' real logo tiles — its own scene, not added to `_SampleStorePlugins`,
        // whose row count PluginStoreBusyGateTests asserts on. Height raised past the dialog's own 820: twelve
        // rows do not fit at rest.
        ["plugin-store-logos"] = (_, _) => new PluginStoreDialog { DataContext = _PluginStoreWithLogos(), Height = 1900 },
        // The store's two busy states (AC-420) — otherwise only reachable while a real download is in flight.
        ["plugin-store-installing"] = (_, _) => _PluginStoreBusy(percent: null, "Downloading 'GitHub Issues' v1.8.0…"),
        ["plugin-store-updating"] = (_, _) => _PluginStoreBusy(percent: 200.0 / 6, "Updating 'Git status' (3 of 6)…"),
        ["manage-stores"] = (_, _) => _ManageStores(),
        ["tasks"] = (_, _) => new DelegatedTasksDialog { DataContext = new ViewModels.DelegatedTasksViewModel() },
        // AC-397, populated: the empty shape is the design-time one and proves nothing about the row templates, and
        // this window is five lists whose failure mode is one of them quietly bound to the wrong collection.
        ["agent-line"] = (_, _) => new AgentLineInspectorDialog { DataContext = _AgentLine() },
        ["set-status"] = (_, _) => new SetStatusDialog { DataContext = new ViewModels.SetStatusDialogViewModel("AC-32 — manual status") },
        ["session"] = (_, _) => new MainWindow { DataContext = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true } },
        // AC-696: two sessions on the desk showing, a third on another. Its own scene because the plain
        // "session" one puts every session on one desk and so cannot show the difference: these two used to
        // lay out as the top row of a 2x2, the other desk's session claiming an empty row underneath.
        ["session-two-desks"] = (_, _) => new MainWindow { DataContext = _TwoSessionDesks() },
        // AC-670: the focus+rail layout, which is the only way to see what a rail tile actually renders as — a
        // miniature of the terminal with the controls you drive it with taken out, one column deep.
        ["focus-rail"] = (_, _) => new MainWindow { DataContext = new ViewModels.CockpitViewModel { GlobalFocusRailLayout = true } },
        // The component has scenes of its own below, but those render it alone — what neither of them can show is
        // whether it survives the collapse in place, which is the half of criterion 6 that is about the sidebar rather
        // than about the chip (AC-543).
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

        // AC-1056: a blocking call and a background one in the same transcript — the pair that used to render
        // identically, which is the whole thing this scene exists to show apart.
        ["session-background-tool"] = (width, height) => new Window { Width = width, Height = height, Content = _BackgroundToolSession() },
        // Rendered rather than only asserted on the parser, because "a link is there" and "it reads as a link, and the
        // one next to it is a different link" are separate claims and only the second is visible (AC-558).
        ["session-links"] = (width, height) => new Window { Width = width, Height = height, Content = _LinkTranscript() },
        // AC-745: the user's own message bubble now carries the same hover copy action the assistant reply
        // already had — focused via the Hovers table below, since the row keeps it at opacity 0 until then.
        ["session-user-row-copy"] = (width, height) => new Window { Width = width, Height = height, Content = _UserRowCopySession() },
        ["assistant-chat-user-row-copy"] = (_, _) => _AssistantChatUserRowCopy(),
        // AC-715: the clarifying-question card in its two states — waiting for an answer, and answered. Two scenes
        // because neither renders the other's surface, and because a passing test can say the labels are bound
        // while the screen still shows raw JSON.
        ["session-question"] = (width, height) => new Window { Width = width, Height = height, Content = _AskUserQuestionSession(answered: false) },
        ["session-question-answered"] = (width, height) => new Window { Width = width, Height = height, Content = _AskUserQuestionSession(answered: true) },
        // AC-489: the approval in plain words, at the two levels that render it differently — Simple, where the
        // command is a fold of its own, and Developer, where the tool chip is that fold. A passing test can say
        // the sentence is bound and the screen still be a wall of JSON with a stray line above it.
        ["session-consent-plain"] = (width, height) => new Window { Width = width, Height = height, Content = _PlainConsentSession(ReadingLevel.Simple) },
        ["session-consent-plain-developer"] = (width, height) => new Window { Width = width, Height = height, Content = _PlainConsentSession(ReadingLevel.Developer) },
        // The fallback: a command that cannot be restated from itself keeps the raw call rather than a guess.
        ["session-consent-plain-fallback"] = (width, height) => new Window { Width = width, Height = height, Content = _PlainConsentFallbackSession() },
        // AC-700: the memory-cap warning in both of its states. The Kill button is the whole point of the second
        // one and it only exists as a binding — a test can say IsOverMemoryCap is true and still be looking at a
        // bar where nothing is drawn.
        ["session-memory-cap-near"] = (width, height) => new Window { Width = width, Height = height, Content = _MemoryCapBar(overCap: false) },
        ["session-memory-cap-over"] = (width, height) => new Window { Width = width, Height = height, Content = _MemoryCapBar(overCap: true) },
        // AC-683: two warnings standing at once — the memory cap actually spent (blocks) and an allowance running
        // low (merely limits it) — stacked, blocking line first, each with its own Dismiss. A single-warning
        // scene cannot show criterion 9's ordering; this is the shape that used to collapse to one visible line.
        ["session-warnings-stacked"] = (width, height) => new Window { Width = width, Height = height, Content = _StackedWarningsSession() },
        ["session-settings-flyout"] = (width, height) => new Window { Width = width, Height = height, Content = _SessionSettingsFlyout(withLiveControls: true) },
        ["session-settings-flyout-no-live-controls"] = (width, height) => new Window { Width = width, Height = height, Content = _SessionSettingsFlyout(withLiveControls: false) },
        // AC-563, staged open by the Hovers table below. See _McpHeader for why each of these four is its own.
        ["session-kind-chip-hover"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader(servers: new HashSet<string> { "youtrack", "depot" }) },
        ["session-mcp-hover"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader(servers: new HashSet<string> { "youtrack", "depot", "cockpit-local-ci", "github-issues" }) },
        ["session-mcp-hover-statusline"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader("AC-563 — wiring the header hover", new HashSet<string> { "youtrack", "depot", "cockpit-local-ci", "github-issues" }) },
        ["session-mcp-hover-unknown"] = (width, height) => new Window { Width = width, Height = height, Content = _McpHeader() },
        // AC-740: the @-mention picker, staged open by the Hovers table below (typing is a caret-driven gesture,
        // not something a scene can pre-stage the way a static transcript can).
        ["session-mention-picker"] = (width, height) => new Window { Width = width, Height = height, Content = _MentionPicker() },
        ["tty"] = (width, height) => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = new ViewModels.TtyViewModel() } },
        // A plain terminal pane (#AC-25/#AC-29): its own scene so the shared header's terminal treatment
        // (kind chip "TTY", no plugin host, no usage pill, shell name only in the cwd tooltip) is verifiable
        // headless — the SDK-only 'session' scene is exactly what let the earlier TTY-header miss slip through.
        ["terminal"] = (width, height) => new Window { Width = width, Height = height, Content = new Views.TtyView { DataContext = ViewModels.TtyViewModel.DesignTerminal() } },
        // Two scenes because the resumable case hides the degraded reason and the degraded case hides the Resume
        // button, so neither renders the other's surface; two views because SessionView and TtyView each spell the
        // banner out separately, and a banner added to only one of them is exactly the kind of half-landed change a
        ["restore-offer"] = (width, height) => _RestorePane(width, height, degraded: false),
        ["restore-offer-degraded"] = (width, height) => _RestorePane(width, height, degraded: true),
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
        // AC-512 criterion 4: the honest notice a browser that would not even start gets, instead of the Guide
        // menu item quietly opening nothing — reuses the same confirmation surface with an OK-only label.
        ["guide-unreachable"] = (_, _) => new ConfirmationDialog
        {
            DataContext = new ViewModels.ConfirmationDialogViewModel(
                "Can't open your browser",
                $"{Core.Configuration.CockpitBrand.ProductName} could not open your browser to show the guide. "
                + $"It lives online at {Core.Configuration.CockpitBrand.GuideUrl} — visit it once you have a "
                + "browser and a connection.",
                "OK"),
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
        // The voice pill's rows are mutually exclusive, so one scene shows one row — and a row with no scene cannot be
        // rendered at all, because a render is asked for by name and nothing walks this table.
        ["voice-overlay-listening"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Listening),
        ["voice-overlay-preparing"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Preparing, "Downloading the speech model (1.1 of 1.6 GB)", progress: 0.7),
        ["voice-overlay-transcribing"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Transcribing),
        ["voice-overlay-speaking"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Speaking),
        ["voice-overlay-unavailable"] = (_, _) => _VoiceOverlay(ViewModels.VoiceOverlayState.Unavailable, "Open mic is on"),

        // Wrapped in a small fixed Window rather than the default 1100x760 (the "tty"/"terminal" scenes' own approach)
        // because this control is a sidebar chip, not a full pane — a full-size render would show mostly empty
        // background (AC-543).
        ["assistant-indicator-ready"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Ready),
        ["assistant-indicator-listening"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Listening),
        ["assistant-indicator-listening-continuously"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously),
        ["assistant-indicator-thinking"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Thinking),
        // The two states that moved off the floating voice pill (2026-08-08). Preparing is rendered with a step
        // and a percentage, which is the whole reason it is not a differently-worded Transcribing.
        ["assistant-indicator-transcribing"] = (_, _) => _AssistantIndicator(Cockpit.Core.Assistant.AssistantActivity.Transcribing),
        ["assistant-indicator-preparing"] = (_, _) => _AssistantIndicator(
            Cockpit.Core.Assistant.AssistantActivity.Preparing,
            preparationStatus: "Downloading speech model", preparationProgress: 0.63),
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
        // indicator is a badge, this is where the conversation actually reads.
        ["assistant-chat"] = (_, _) => _AssistantChat(withConversation: true),
        ["assistant-chat-empty"] = (_, _) => _AssistantChat(withConversation: false),
        ["assistant-chat-speak-off"] = (_, _) => _AssistantChat(withConversation: true, speakReplies: false),
        ["assistant-chat-always-on"] = (_, _) => _AssistantChat(withConversation: true, alwaysOn: true),
        // AC-953: the same chat surface in its other host — docked into the right-hand rail, inside MainWindow,
        // with Undock where the floating window has Close. The scene that shows what "docked is ordinary cockpit
        // UI" actually looks like: in the column structure beside the session content, no chrome of its own.
        ["assistant-docked"] = (_, _) => _AssistantDockedInTheRail(),
        // AC-960 criterion 15: a plugin-registered panel (a stand-in for GitHubPullRequestsWidget — Cockpit.App
        // does not reference that store-distributed plugin's assembly) at the rail's minimum and maximum content
        // widths, plus the collapsed rail carrying both the Assistant's tab and this one.
        ["dock-panel-pull-requests-min"] = (_, _) => _DockRailWithPullRequestsPanel(Cockpit.Core.Layout.LayoutSettings.MinDockRailWidth),
        ["dock-panel-pull-requests-max"] = (_, _) => _DockRailWithPullRequestsPanel(Cockpit.Core.Layout.LayoutSettings.MaxDockRailWidth),
        ["dock-rail-collapsed-two-tabs"] = (_, _) => _DockRailCollapsedWithTwoTabs(),
        // AC-740 addendum: the picker in the pop-out's own composer, staged open by the Hovers table below.
        ["assistant-chat-mention-picker"] = (_, _) => _AssistantChatMentionPicker(),
        // AC-683 criteria 1-3: the usage-pill row and the stacked warning bar, both new to this window — it had
        // neither before, which was the whole premise of the ticket (a running-out allowance was invisible on
        // the assistant's one and only surface).
        ["assistant-chat-warnings"] = (_, _) => _AssistantChatWithWarnings(),
        ["assistant-chat-question"] = (_, _) => _AssistantChatQuestion(),
        // AC-1018: the broker route (AssistantAgentGateway.AskStructuredQuestionAsync) builds its row with Kind =
        // Question, not ToolUse like the scene above — the gap that let the card silently never render live.
        ["assistant-chat-question-broker"] = (_, _) => _AssistantChatBrokerQuestion(),

        // AC-776 Deel 1: the session-status pill in all six SessionStatus values, and the same pill's "⋯" flyout
        // staged open by the Hovers table below (criterion 12's four scenes: all-statuses, the opened session
        // list, the 340px narrow wrap, and Deel 2's opened history/export dropdown).
        ["assistant-chat-session-pill"] = (_, _) => _AssistantChatSessionPill(sessionCount: 6),
        ["assistant-chat-session-pill-list-open"] = (_, _) => _AssistantChatSessionPill(sessionCount: 6),
        ["assistant-chat-session-pill-narrow"] = (_, _) => _AssistantChatSessionPill(sessionCount: 6, width: 340),
        ["assistant-chat-history-dropdown-open"] = (_, _) => _AssistantChat(withConversation: true),

        // AC-566 criterion 8: the preview window gated behind Confirm(), with a wide screenshot and a narrow
        // one — the two extremes Stretch="Uniform" has to lay out, rather than only whatever aspect ratio a
        // developer's own screen happens to produce.
        ["screenshot-preview-wide"] = (_, _) => Views.ScreenshotPreviewWindow.Build(_StandInPng(1600, 500), "personal - webshop"),
        ["screenshot-preview-narrow"] = (_, _) => Views.ScreenshotPreviewWindow.Build(_StandInPng(500, 1400), "personal - webshop"),

        // AC-509: the first-run wizard shell on its first step, wired the way both production call sites build it
        // — the epic's own four-slot plan, so this baseline is what "Step 1 of 4" and the Depot placeholder
        // actually render as, not just the shell in isolation. Iron Law #9.
        ["first-run-wizard"] = (_, _) => new Views.Onboarding.FirstRunWizardWindow
        {
            DataContext = new ViewModels.Onboarding.FirstRunWizardViewModel(
                [new Views.Onboarding.WelcomeStep()],
                ViewModels.Onboarding.FirstRunWizardViewModel.EpicPlan),
        },

        // Stage provider availability states directly so this headless scene does not depend on a store fetch
        // (AC-510[b]).
        ["provider-step-catalogue"] = (_, _) => _AsWindow(_ProviderStepCatalogue(), 640, 560),
        // Criterion 3: offline is a plain statement, not styled as an error — the local-providers note above it
        // is unaffected either way.
        ["provider-step-offline"] = (_, _) => _AsWindow(_ProviderStepOffline(), 640, 480),
        // Criterion 2's remaining two shapes — a fresh install and a batch failure — plus the "half succeeded"
        // summary line, all only reachable in the real app after InstallSelectedCommand actually runs.
        ["provider-step-install-outcomes"] = (_, _) => _AsWindow(_ProviderStepInstallOutcomes(), 640, 560),


        // AC-511 criterion 7: the work-kind step in the shell it actually lives in, at the shell's own fixed size,
        // in both the state that fits and the one that does not. A work kind that pre-ticks six plugins is the
        // case where the confirm button can be pushed past the bottom edge, and nobody sees that on three rows.
        ["first-run-work-kind"] = (_, _) => _WorkKindWizard(pluginCount: 3),
        ["first-run-work-kind-long"] = (_, _) => _WorkKindWizard(pluginCount: 6),
    };

    private static Window _WorkKindWizard(int pluginCount)
    {
        var rows = Enumerable.Range(1, pluginCount).Select(index => new ViewModels.Onboarding.WorkKindPluginRowViewModel(
            name: WorkKindPluginNames[(index - 1) % WorkKindPluginNames.Length],
            version: $"1.{index}.0",
            author: "Cockpit",
            from: $"https://plugins.example.org/index.json → pack-{index}/pack-1.{index}.0.zip",
            checksum: $"9f2c4b1ea7d05836c1b4e0f9a3d7c25e8b6041fd93a7e2c5b80d1a6a4e37c9b{index:D2}",
            isSelected: true));

        var step = new Views.Onboarding.WorkKindStep(new ViewModels.Onboarding.WorkKindStepViewModel(rows));

        return new Views.Onboarding.FirstRunWizardWindow
        {
            DataContext = new ViewModels.Onboarding.FirstRunWizardViewModel([step]),
        };
    }

    // AC-937: a CockpitViewModel with a few side-buttons registered through the sink, the same route a real plugin
    // uses — Autopilot and Open PRs pinned (the shipped default for those two), YouTrack and Workflows collapsed,
    // one of them (Open PRs) carrying a badge so the flyout also shows a live counter.
    private static Window _PluginsMenuScene()
    {
        var cockpit = new ViewModels.CockpitViewModel();
        var sink = (Plugins.IPluginContributionSink)cockpit;

        sink.AddPluginSideButton("autopilot", "Autopilot", () => { });
        var openPrsBadge = new SideMenuButtonBadge { Primary = 19, Secondary = 0 };
        sink.AddPluginSideButton("github-pull-requests", "Open PRs", () => { }, openPrsBadge);
        sink.AddPluginSideButton("youtrack", "YouTrack", () => { });
        var issuesBadge = new SideMenuButtonBadge { Primary = 3 };
        sink.AddPluginSideButton("github-issues", "GitHub Issues", () => { }, issuesBadge);

        cockpit.ApplyPluginMenuPreference("autopilot", menuOrder: 0, hiddenInMenu: false, pinnedToSidebar: true);
        cockpit.ApplyPluginMenuPreference("github-pull-requests", menuOrder: 1, hiddenInMenu: false, pinnedToSidebar: true);
        cockpit.ApplyPluginMenuPreference("youtrack", menuOrder: 2, hiddenInMenu: false, pinnedToSidebar: false);
        cockpit.ApplyPluginMenuPreference("github-issues", menuOrder: 3, hiddenInMenu: false, pinnedToSidebar: false);

        return new MainWindow { DataContext = cockpit };
    }

    private static readonly string[] WorkKindPluginNames =
        ["GitHub Issues", "GitHub Pull Requests", "YouTrack", "Weather", "Time Tracking", "Invoices"];

    // Every scene name a render can be asked for, this table's own plus the selection surface's — that one keeps
    // its names with the scene because its modes are states the surface is driven into after it is shown, not
    // windows that open in them, so the name means nothing until then.
    internal static IReadOnlyList<string> SceneNames { get; } = [.. Scenes.Keys, .. ScreenshotSelectionScene.Names];

    // The window a scene asks for, on screen and in the state the name describes. Its own step so that anything
    // looking at a scene — a render here, the theme baseline in the view tests — reaches it by the same route,
    // rather than a second copy that can drift out of step with this one.
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

    // Both attach to a host that does not exist until the window is up, so — like the selection surface's modes above —
    // they are opened here rather than built into the scene.
    private static readonly Dictionary<string, Action<Window>> Hovers = new(StringComparer.Ordinal)
    {
        ["session-settings-flyout"] = window => _OpenFlyout(window, "SessionSettingsButton"),
        ["session-settings-flyout-no-live-controls"] = window => _OpenFlyout(window, "SessionSettingsButton"),
        ["workspace-layout-flyout"] = window => _OpenFlyout(window, "WorkspaceLayoutButton"),
        ["help-menu"] = window => _OpenFlyout(window, "HelpButton"),
        ["plugins-menu"] = window => _OpenFlyout(window, "PluginsMenuButton"),
        ["session-kind-chip-hover"] = window => _OpenTooltip(window, "KindChip"),
        ["session-mcp-hover"] = window => _OpenTooltip(window, "ActivityColumn"),
        ["session-mcp-hover-statusline"] = window => _OpenTooltip(window, "ActivityColumn"),
        ["session-mcp-hover-unknown"] = window => _OpenTooltip(window, "ActivityColumn"),
        // AC-745: the rowActions fade lives at opacity 0 until the row's Border is hovered or, as here,
        // keyboard-focused — focusing the button itself is the cheapest way to trigger the same :focus-within.
        ["session-user-row-copy"] = window => _Named<Button>(window, "UserRowCopyButton").Focus(),
        ["assistant-chat-user-row-copy"] = window => _Named<Button>(window, "UserRowCopyButton").Focus(),
        // AC-740: opening the picker is a caret-driven gesture (MentionPickerViewModel.OnTextChanged), not a
        // property a scene can stage ahead of the window existing.
        ["session-mention-picker"] = _OpenMentionPicker,
        ["assistant-chat-mention-picker"] = _OpenAssistantChatMentionPicker,
        // AC-776: the session-pill's own "⋯" list, and Deel 2's merged history/export dropdown.
        ["assistant-chat-session-pill-list-open"] = window => _OpenFlyout(window, "SessionListButton"),
        ["assistant-chat-history-dropdown-open"] = window => _OpenFlyout(window, "HistoryButton"),
    };

    private static void _OpenMentionPicker(Window window)
    {
        if (window.Content is SessionView { DataContext: SessionViewModel viewModel })
        {
            viewModel.InputText = "@Session";
            viewModel.MentionPicker.OnTextChanged("@Session", "@Session".Length);
        }
    }

    private static void _OpenAssistantChatMentionPicker(Window window)
    {
        // On the view, not the window: the chat surface is `AssistantChatView` since AC-952, and it is what a
        // docked assistant (AC-953) will be found by — there is no AssistantChatWindow around it there.
        if (window.FindDescendantOfType<AssistantChatView>() is { DataContext: ViewModels.AssistantChatViewModel viewModel })
        {
            viewModel.InputText = "@Session";
            viewModel.MentionPicker.OnTextChanged("@Session", "@Session".Length);
        }
    }

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

    // Its own step so the table above can be held to a test — a scene that stopped building was otherwise found by
    // whoever next asked for a render, which on this surface has meant finding it after it shipped.
    internal static Window BuildScene(string? scene, int width = DefaultWindowWidth, int height = DefaultWindowHeight)
    {
        Window window;
        if (scene is null)
        {
            window = new MainWindow { DataContext = new ViewModels.CockpitViewModel() };
        }
        else if (Scenes.TryGetValue(scene, out var build))
        {
            window = build(width, height);
        }
        else if (ScreenshotSelectionScene.Covers(scene))
        {
            window = ScreenshotSelectionScene.Build(scene, width, height);
        }
        else
        {
            // A misspelled name used to fall through to the main window, so the render succeeded and showed a screen
            // nobody asked about — the one failure a picture cannot reveal, since it looks like a picture (AC-1235).
            throw new ArgumentException(
                $"Unknown scene \"{scene}\". Known scenes: {string.Join(", ", SceneNames.Order(StringComparer.Ordinal))}.",
                nameof(scene));
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
    // picker, the box holds the bare identifier rather than a path, and "Choose…" has stepped aside.
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

    // The resource section with two rows, in the state that paints the most of it (AC-414): a Memory row with a plugin
    // source picked (mirroring the memory-source scene above, folded into this section instead of a dedicated field),
    // and a Reference row that is both machine-bound and broken — the two things AC-485 requires be visible (AC-486).
    private static ProjectDialog _ProjectEditorWithResources()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot project", "depot"));

        var memoryRow = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "cockpit", "Team notes");
        memoryRow.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        viewModel.ResourceRows.Add(memoryRow);

        // Reaching sessions, deliberately, even though an unticked row would show the checkbox contrast: a row that is
        // switched off is never probed at all, so "switched off" and "broken" cannot both be true of it (AC-605).
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices,
            ProjectResourceRole.Reference,
            OperatingSystem.IsWindows()
                ? @"C:\Users\raymond\.cockpit-screenshot-scene-does-not-exist\handbook.md"
                : "/home/raymond/.cockpit-screenshot-scene-does-not-exist/handbook.md",
            "Old handbook")
        {
            IsBroken = true,
            Scope = ProjectResourceScope.Machine,
        });

        // Taller than the dialog opens, the way the memory-source scene already is — clamped by DialogScreenClamp
        // to 90% of the headless screen either way, but still what pushes the resource section as far above that
        // ceiling as this dialog's other sections leave room for.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    // Its own scene rather than more rows crammed into that one, the same reason
    // `_ProjectEditorWithInstructionsSendAlong` already exists apart from it — a coordinator review round asked for
    // "all four scopes and the fix action visible" and explicitly offered this as one of two acceptable answers
    private static ProjectDialog _ProjectEditorWithResourceScopes()
    {
        var sourceDirectory = OperatingSystem.IsWindows() ? @"C:\Users\raymond\Cockpit" : "/home/raymond/Cockpit";
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = sourceDirectory };
        // Every bit of chrome this scene does not need earns back room under DialogScreenClamp's own fold — see
        // _ProjectEditorWithMemorySourceReachability's own remarks on the same design-time sample rows.
        viewModel.AdditionalInfo.Clear();

        // Staged to match exactly what the real diagnostics pass computes for this reference (an absolute path inside
        // sourceDirectory that does not exist) — see this method's own doc comment on why both must agree.
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices,
            ProjectResourceRole.Reference,
            Path.Combine(sourceDirectory, "docs", "onboarding.md"),
            "Onboarding (hand-typed)")
        {
            IsBroken = true,
            Scope = ProjectResourceScope.Machine,
            // Always "/"-separated regardless of platform (ProjectResourcePathPortability.ToStoredReference's own
            // FIX 5) — not Path.Combine("docs", "onboarding.md"), which would carry a backslash on Windows and
            // disagree with what the real pass actually computes there.
            RepoRelativeFix = "docs/onboarding.md",
        });

        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Reference, "~/Notes/team-conventions.md", "Team conventions")
        {
            IsBroken = true,
            Scope = ProjectResourceScope.Home,
        });

        // Taller than the dialog opens, the same reason every scene in this family already gives — clamped by
        // DialogScreenClamp to 90% of the headless screen regardless.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    private static ProjectDialog _ProjectEditorWithOwnership()
    {
        var project = new Project("proj-eve-workbench", "EVE Workbench") { BehaviorPrompt = "Community-platform. Multi-user is inherent to the product." };

        var fieldOwnership = new Dictionary<HostProjectField, ProjectFieldOwnership?>
        {
            [HostProjectField.Name] = new ProjectFieldOwnership("Depot — Work", IsEditable: false),
            [HostProjectField.Behavior] = new ProjectFieldOwnership("Depot — Work", IsEditable: true),
        };

        // Run on a pool thread rather than a bare .GetAwaiter().GetResult() on this (dispatcher-context-carrying)
        // thread: CreateAsync's own resource-diagnostics pass does a genuine Task.Run + ConfigureAwait(true), whose
        // continuation would otherwise be posted back to a headless dispatcher nothing is pumping — a deadlock
        var viewModel = Task.Run(() => ProjectDialogViewModel.CreateAsync(
            project, new _FakeSessionProfileStore(), new _FakeMcpServerCatalog(), fieldOwnership: fieldOwnership))
            .GetAwaiter().GetResult();

        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    private sealed class _FakeSessionProfileStore : ISessionProfileStore
    {
        public Task<IReadOnlyList<SessionProfile>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SessionProfile>>([]);

        public Task SaveAsync(IReadOnlyList<SessionProfile> profiles, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class _FakeMcpServerCatalog : IMcpServerCatalog
    {
        public Task<IReadOnlyList<McpServerConfig>> GetServersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpServerConfig>>([]);

        public Task<IReadOnlyList<McpServerConfig>> GetServersForProjectAsync(string? projectId, CancellationToken cancellationToken = default) =>
            GetServersAsync(cancellationToken);
    }

    // Built directly against `ProjectResourceRowViewModel`'s own constructor — passing `familyInstanceChoicesByKey`
    // straight in — rather than through `ProjectDialogViewModel.CreateAsync`: `MemorySourceFamilyInstances` only has a
    // private setter, so a scene (outside the view model's own assembly boundary in every way but the (AC-499, #9).
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
            new("Depot (acme)", "depot.acme"),
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

    // AC-502: a loaded picker, two locations so the list itself (not just a single row) is verifiable — one with a
    // detail line, one without, since ProjectMemorySourceLocation.Detail is optional and both must render cleanly
    // (AC-499).
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPicker()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Acme",
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
            "Depot project — Acme",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.AuthorizationRequired),
            signInAsync: _ => Task.FromResult(true));
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // The failed-load state (AC-502 criterion 5): says what went wrong rather than showing nothing.
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerError()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — Acme",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Failed(
                "Couldn't reach the Depot server — check the connection's URL in Depot's settings.")));
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // AC-499: ten locations (Raymond's own krahwinkel-it instance mixes this many Depot projects and brains), the
    // seventh carrying the Reference the row already had — proves the pre-selection, its "Current" badge, the scroll
    // into view (this list does not fit the window's own resting height), and — via the one entry with no Detail line
    private static MemorySourceLocationPickerDialog _MemorySourceLocationPickerCurrent()
    {
        var viewModel = new ViewModels.MemorySourceLocationPickerViewModel(
            "Depot project — krahwinkel-it",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success(
            [
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("cockpit", "Cockpit", "Project · 21 documents · updated 26 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("acme", "Acme", "Project · 8 documents · updated 20 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("ai-hub", "AI-Hub", "Project · 5 documents · updated 18 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("olaf", "Olaf", "Brain"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("zyra", "Zyra", "Brain"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("moneybird-toolbox", "Moneybird-Toolbox", "Project · 3 documents · updated 12 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("eve-workbench", "EVE Workbench", "Project · 42 documents · updated 29 Jul 2026"),
                // No Detail — right after the pre-selected row, so both land in the same scrolled-into-view
                // viewport and a height difference between them (point 3) is actually on screen, not assumed away.
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("ddd-template", "DDD-Template"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("handbook-processor", "Handbook", "Project · 14 documents · updated 22 Jul 2026"),
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
            "Depot project — Acme",
            _ => Task.FromResult(Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocationsResult.Success(
            [
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("cockpit", "Cockpit", "Project · 21 documents · updated 26 Jul 2026"),
                new Cockpit.Plugins.Abstractions.Projects.ProjectMemorySourceLocation("olaf", "Olaf", "Brain"),
            ])),
            currentValue: "moved-away");
        viewModel.LoadAsync().GetAwaiter().GetResult();
        return new MemorySourceLocationPickerDialog { DataContext = viewModel };
    }

    // AC-503, Iron Law #9: three of the four states a Memory row's own reachability check can land on, one row each —
    // confirmed, not found, and not signed in — so they are actually visible on screen rather than only asserted in a
    // test (AC-499).
    private static ProjectDialog _ProjectEditorWithMemorySourceReachability()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel();
        // The design-time constructor's own sample "Repository"/"Customer" rows are cleared: three memory rows each
        // carrying their own picker and hint already push this scene's own fold further down than
        // _ProjectEditorWithResources's two rows do, and DialogScreenClamp still caps the window at 90% of the headless
        viewModel.AdditionalInfo.Clear();
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Folder", Scheme: null));
        viewModel.MemorySourceChoices.Add(new ViewModels.MemorySourceChoice("Depot project", "depot"));

        var confirmed = new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Memory, "cockpit", "Team notes");
        confirmed.SelectedMemorySourceChoice = viewModel.MemorySourceChoices[1];
        confirmed.Reachability = ProjectMemorySourceReachability.Confirmed;
        confirmed.ReachabilityDetail = "24 documents, last changed 2 hours ago";
        viewModel.ResourceRows.Add(confirmed);

        // NotSignedIn (a colour — CockpitStatusWaitingBrush — this dialog has never shown before AC-503) is placed
        // where it is guaranteed to render in full; NotFound reuses IsBroken's own established
        // red/CockpitStatusErrorBrush treatment, which a render already proved legible for this dialog before this
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

    // A single Instructions row with "Send along" ticked (AC-486): its own scene rather than a third row folded into
    // `_ProjectEditorWithResources` above — see that method's own remarks on why combining the two pushed a hint *that*
    // scene already had to prove visible below this dialog's fold.
    private static ProjectDialog _ProjectEditorWithInstructionsSendAlong()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Instructions, "docs/handbook.md", "House conventions")
        {
            SendsContent = true,
        });

        // Request 1500px so DialogScreenClamp's 90% ceiling still shows every preceding section and two full rows.
        return new ProjectDialog { DataContext = viewModel, Height = 1500 };
    }

    // A single Instructions row pointing at a likely secrets location (AC-612): its own scene rather than a third row
    // folded into `_ProjectEditorWithResources` or a fifth into `_ProjectEditorWithResourceScopes` — both already sit
    // at their own fold ceiling per their own remarks.
    private static ProjectDialog _ProjectEditorWithSecretPath()
    {
        var viewModel = new ViewModels.ProjectDialogViewModel { SourceDirectory = "/home/raymond/Cockpit" };
        viewModel.ResourceRows.Add(new ViewModels.ProjectResourceRowViewModel(
            viewModel.MemorySourceChoices, ProjectResourceRole.Instructions, "~/.ssh/id_rsa", "SSH key"));

        // 1500 rather than a value nearer this scene's own resting height — the same reasoning
        // _ProjectEditorWithInstructionsSendAlong already gives for its own single row.
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
        // Use the bundled provider config because this design-time path bypasses legacy ClaudeConfig migration.
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

    // A Codex-shaped plugin profile that stores one of its four declared start options and leaves three unset — the
    // state the profile editor produces since AC-1102, when it stopped persisting a default nobody chose. Both
    // control shapes are here: Sandbox and Approval draw combo boxes (one set, one not), Model and Effort declare no
    // choices and draw text boxes, so the unset placeholder is visible in each of the two places it can appear.
    // Rendered in the Options dialog rather than the standalone editor: that is where the operator meets this block,
    // and its detail column is the narrower of the two — the width at which an unset row would truncate if it were
    // going to. Taller than the page rests at, so SESSION DEFAULTS is above the fold.
    private static OptionsDialog _ManageProfilesPluginOptionDefaults()
    {
        var profile = new SessionProfile(
            "codex",
            new PluginProviderConfig(_FakeCodexProviderRegistry.ProviderId, "{}"),
            Purpose: "Codex CLI profile",
            Defaults: new ProfileDefaults(string.Empty, string.Empty, string.Empty)
            {
                OptionDefaults = new Dictionary<string, string> { ["sandbox"] = "workspace-write" },
            });

        var profiles = new ViewModels.ManageProfilesDialogViewModel();
        var editable = new ViewModels.EditableProfileViewModel(
            profile,
            isLoggedIn: true,
            providers: SessionProviderCatalog.AllProviders(new _FakeCodexProviderRegistry()),
            pluginProviderRegistry: new _FakeCodexProviderRegistry());
        profiles.Profiles.Clear();
        profiles.Profiles.Add(editable);
        profiles.SelectedProfile = editable;

        var dialog = new OptionsDialog { DataContext = new ViewModels.CockpitViewModel { Profiles = profiles }, Height = 1500 };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "profiles");

        return dialog;
    }

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

    // AC-1019: Options dialog on the Profiles category, at a resting height (700) well short of the selected
    // profile's detail form — the same design-time sample ManageProfilesDialogViewModel's own constructor seeds,
    // so the list has one entry and the detail column is the full IDENTITY..ENVIRONMENT VARIABLES form.
    private static OptionsDialog _OptionsProfilesPage()
    {
        var cockpit = new ViewModels.CockpitViewModel { Profiles = new ViewModels.ManageProfilesDialogViewModel() };
        var dialog = new OptionsDialog { DataContext = cockpit, Height = 700 };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "profiles");

        return dialog;
    }

    // A main window with one session running, so `ShowSessionGrid` shows the toolbar the workspace ⚙ lives in.
    private static MainWindow _MainWindowWithOneSession(int width, int height)
    {
        var cockpit = new ViewModels.CockpitViewModel();
        cockpit.Sessions.Add(new ViewModels.SessionViewModel { Title = "Session", WorkspaceId = cockpit.Workspaces.Active!.Id });
        return new MainWindow { DataContext = cockpit, Width = width, Height = height };
    }

    // Renders the Options dialog with one of its sidebar categories selected (AC-1000: the sidebar's CategoryNav
    // ListBox replaced the old per-tab TabControl), so a category other than the first one can be verified without a
    // display.
    private static OptionsDialog _OptionsOnTab(string header)
    {
        var dialog = new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");

        nav.SelectedItem = nav.Items
            .OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, header, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The Options sidebar has no '{header}' category.");

        return dialog;
    }

    // The Assistant category, enabled rather than dimmed under the off master switch, so its profile row renders
    // filled in rather than showing the design-time graph's "no profile store" fallback.
    private static OptionsDialog _OptionsAssistantPage()
    {
        var cockpit = new ViewModels.CockpitViewModel();
        cockpit.AssistantOptions.IsEnabled = true;
        // The design-time graph has no profile store, so the row would otherwise render an "Edit…" button with
        // nothing beside it — a state no real cockpit has (an unset slot fills in its reason instead).
        cockpit.AssistantOptions.ProfileLabel = "Claude (assistant) · claude · sonnet";

        var dialog = new OptionsDialog { DataContext = cockpit };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "assistant");

        return dialog;
    }

    // AC-1000: consent-bypass moved from the old Assistant sub-page to Security. One recognised consent-bypass row
    // and one orphaned one (#K11: a stored key — "kubernetes" — this build no longer recognises now that a plugin
    // source keys as "plugin:&lt;id&gt;") — the scene moved with the setting, so the orphan-row state stays covered.
    private static OptionsDialog _OptionsSecurityConsentBypassPage()
    {
        var cockpit = new ViewModels.CockpitViewModel();
        // "Allow all" off (#AC-637), because the per-source rows below are what this shot is of — it hides them.
        cockpit.AssistantOptions.ConsentBypassAll = false;
        cockpit.AssistantOptions.ConsentBypassSources.Add(
            new ViewModels.ConsentBypassSourceViewModel(
                Cockpit.Core.Consent.ConsentSourceCatalog.TerminalMcp, Cockpit.Core.Consent.ConsentSourceCatalog.TerminalMcp)
            { BypassLowRisk = true });
        cockpit.AssistantOptions.ConsentBypassSources.Add(
            new ViewModels.ConsentBypassSourceViewModel("kubernetes", "kubernetes", isOrphan: true)
            { BypassLowRisk = true, BypassDangerous = true });

        var dialog = new OptionsDialog { DataContext = cockpit };
        var nav = dialog.FindControl<ListBox>("CategoryNav")
            ?? throw new InvalidOperationException("The Options dialog has no 'CategoryNav' sidebar to select on.");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().First(item => item.Tag as string == "security");

        return dialog;
    }

    // Renders the cockpit with a couple of toolbar actions seeded (AC-91) so the quick-action buttons are verifiable
    // headless. Since AC-772 they sit on the workspace tab strip rather than in the session grid's own header, which
    // is what this scene now shows.
    private static MainWindow _ToolbarActions()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "docker", new Cockpit.Plugins.Abstractions.ToolbarAction("Docker settings", Material.Icons.MaterialIconKind.Docker, () => Task.CompletedTask)));
        cockpit.PluginToolbarActions.Add(new Plugins.PluginToolbarAction(
            "kubernetes", new Cockpit.Plugins.Abstractions.ToolbarAction("Kubernetes settings", Material.Icons.MaterialIconKind.Kubernetes, () => Task.CompletedTask)));

        return new MainWindow { DataContext = cockpit };
    }

    // AC-772 criteria 15 and 20: the Projects workspace in one layout, since only a render per layout shows whether
    // each holds together and that all three carry the "From your team" section.
    private static MainWindow _ProjectsWorkspace(Cockpit.Core.Projects.ProjectsLayoutMode layout)
    {
        var cockpit = new ViewModels.CockpitViewModel();

        // Staged onto the cockpit's own view model, which is the one the view binds to; a headless render has no
        // `ISharedProjectSource` to list from.
        cockpit.Projects.CardActions = new ViewModels.ProjectCardActions(
            cockpit.StartProjectSessionCommand,
            cockpit.NewSessionForProjectCommand,
            cockpit.EditProjectCommand,
            cockpit.OpenProjectFolderCommand,
            cockpit.ShareProjectCommand,
            cockpit.SyncProjectNowCommand);
        cockpit.Projects.StageDesignSample();
        cockpit.Projects.StageDesignSharedProjects();
        cockpit.Projects.LayoutMode = layout;

        cockpit.Workspaces.OpenWorkspaceAsync(Cockpit.Core.Workspaces.WorkspaceType.Projects.Id).GetAwaiter().GetResult();

        return new MainWindow { DataContext = cockpit };
    }

    // A screenshot-shaped PNG at whatever aspect ratio a preview scene needs, from the same stand-in drawing the
    // selection surface's own scenes use.
    private static byte[] _StandInPng(int width, int height)
    {
        using var bitmap = StandInDesktop.Draw(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
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

    // The activity column carries the MCP servers instead: named, unknown (criterion 6 — never an empty list), and with
    // an agent's statusline in the column (criterion 8 — the list must not leave with the words it replaced) (AC-146,
    // AC-563).
    private static SessionView _McpHeader(string? statusline = null, IReadOnlySet<string>? servers = null)
    {
        var viewModel = new SessionViewModel { Statusline = statusline ?? string.Empty, McpServerSelection = servers };
        // Off the selection, never typed out beside it — a scene that staged its own count would be free to stage
        // one the hover disagrees with, which is the very thing these renders exist to rule out.
        viewModel.Status = viewModel.ConnectedStatusLine;

        return new SessionView { DataContext = viewModel };
    }

    // AC-740: a handful of paths under a working directory that would resolve to it in a real repo, so the row
    // template (bold name, dimmed parent directory, trailing '/' on a directory row) has something to show.
    private sealed class _SampleMentionFileSource : IMentionFileSource
    {
        public Task<IReadOnlyList<string>> GetPathsAsync(string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<string>>([
                "src/Views/SessionView.axaml",
                "src/Views/SessionView.axaml.cs",
                "src/ViewModels/SessionViewModel.cs",
                "src/ViewModels/",
            ]);
    }

    private static SessionView _MentionPicker()
    {
        var viewModel = new SessionViewModel(new _SampleMentionFileSource()) { WorkingDirectory = "/repo" };
        return new SessionView { DataContext = viewModel };
    }

    // AC-562: the sliders flyout with the reading level in it, in both states criterion 3 separates — a
    // provider that declares live controls, and one that declares none, where the button used to disappear
    // and take the reading level with it.
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

    // AC-745: a plain user message, the row the copy button was missing from — the Hovers table focuses the
    // button once this window is up so the fade-in the row shares with the assistant reply actually shows.
    private static SessionView _UserRowCopySession()
    {
        var viewModel = new SessionViewModel { Title = "personal - webshop" };
        viewModel.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "remember to check the deploy logs"));
        return new SessionView { DataContext = viewModel };
    }

    // AC-745: the same row on the assistant pop-out, proving the shared TranscriptRowView control (AC-722)
    // carries the button here too rather than assuming it — AC-715 found this window keeping its own copy of
    // a row once before.
    private static AssistantChatWindow _AssistantChatUserRowCopy()
    {
        var session = new SessionViewModel { Title = "personal - webshop" };
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "remember to check the deploy logs"));

        var host = new _FakeAssistantSessionHost { Session = session, Activity = Cockpit.Core.Assistant.AssistantActivity.Ready };
        var viewModel = new ViewModels.AssistantChatViewModel(host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // The session's own warning bar approaching its memory cap, and past it (AC-661/AC-700). Driven through the
    // real `ReportMemoryAgainstCap`, not by setting the bar's text, so what is on screen is what a sample produces.
    private static SessionView _MemoryCapBar(bool overCap)
    {
        const long Cap = 512L * 1024 * 1024;
        var viewModel = new SessionViewModel { Title = "personal - webshop", MemoryCapBytes = Cap };

        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Running the build." });
        viewModel.ReportMemoryAgainstCap((long)(Cap * (overCap ? 1.12 : 0.86)));

        return new SessionView { DataContext = viewModel };
    }

    // AC-683: the memory cap actually spent and a weekly allowance running low, standing at once — driven through
    // the same real ApplyUsage/ReportMemoryAgainstCap every other warning scene uses, not by fabricating Warnings
    // entries, so what is on screen is what the mechanism itself produces.
    private static SessionView _StackedWarningsSession()
    {
        const long Cap = 512L * 1024 * 1024;
        var viewModel = new SessionViewModel { Title = "personal - webshop", MemoryCapBytes = Cap };

        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Running the build." });
        viewModel.ApplyUsage(
            [
                new PluginUsageSignal("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" },
                new PluginUsageSignal("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" },
            ],
            [
                new PluginUsageReading("context", 55, null),
                new PluginUsageReading("weekly", 95, DateTimeOffset.Now.AddDays(3)),
            ]);
        viewModel.ReportMemoryAgainstCap((long)(Cap * 1.12));

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

    // AC-1056: four tool calls that used to be indistinguishable — one blocking, one still running in the
    // background, one whose background task the ledger has stopped reporting, and one that came back an error.
    private static SessionView _BackgroundToolSession()
    {
        var viewModel = new SessionViewModel { Title = "personal - webshop" };

        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Building, and starting the dev server alongside it." });

        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_fg", ToolName = "Bash", InputJson = """{"command":"dotnet build"}""" });
        viewModel.Apply(new ToolResult { SessionId = "s1", ToolUseId = "toolu_fg", Content = "Build succeeded. 0 Warning(s), 0 Error(s)", IsError = false });

        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_bg", ToolName = "Bash", InputJson = """{"command":"npm run dev","run_in_background":true}""" });
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_done", ToolName = "Bash", InputJson = """{"command":"npm run build","run_in_background":true}""" });
        viewModel.Apply(new BackgroundTasksChanged
        {
            SessionId = "s1",
            Tasks = [new BackgroundTask("b706pro1i", BackgroundTaskKind.Shell, "npm run dev")],
        });
        viewModel.Apply(new ToolResult { SessionId = "s1", ToolUseId = "toolu_bg", Content = "Command running in background with ID: b706pro1i.", IsError = false });
        viewModel.Apply(new ToolResult { SessionId = "s1", ToolUseId = "toolu_done", Content = "Command running in background with ID: b9kk2mfe1.", IsError = false });

        // The MCP half (AC-1053): nothing in the input asked for the background, the result announces it.
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_mcp", ToolName = "run_local_checks", InputJson = """{"job":"build"}""" });
        viewModel.Apply(new ToolResult
        {
            SessionId = "s1",
            ToolUseId = "toolu_mcp",
            Content = "transport dropped mid-call after it was moved to the background as task kswv2rq5q",
            IsError = true,
        });

        return new SessionView { DataContext = viewModel };
    }

    // AC-489: an approval as the people this is for meet it. The call carries a `description` the model wrote
    // about its own request, deliberately: the sentence on screen must be visibly not that.
    private static SessionView _PlainConsentSession(ReadingLevel level) => _ConsentSession(
        level,
        "June is booked. Three invoices are still sitting in the inbox.",
        """{"command":"mv KPN-2026-06.pdf Vattenfall-juni.pdf Hosting-Q2.pdf ./archive/2026-06/","description":"Tidying up the inbox, nothing important"}""");

    // Two deletions joined into one line: every token here is a path or a verb this could otherwise read, so the
    // sequence itself is the only thing standing between it and "Delete 4 files" — a sentence that would be
    // counting the shell's own operator as a file, on the one verb where being wrong costs the most.
    private static SessionView _PlainConsentFallbackSession() => _ConsentSession(
        ReadingLevel.Simple,
        "Let me clear out what is left over.",
        """{"command":"rm ./inbox/notes.txt && rm ./inbox/old-draft.txt"}""");

    private static SessionView _ConsentSession(ReadingLevel level, string leadIn, string input)
    {
        var viewModel = new SessionViewModel { Title = "invoices", ReadingLevel = level };
        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = leadIn });
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_c1", ToolName = "Bash", InputJson = input });
        viewModel.Apply(new PermissionRequested { SessionId = "s1", ToolUseId = "toolu_c1", ToolName = "Bash", InputJson = input });

        return new SessionView { DataContext = viewModel };
    }

    // AC-715: an AskUserQuestion as it arrives — over the permission callback, with the agent's own options in the
    // payload. Two questions, one single-select and one multi-select, because they render the same tick column and
    // only the multi-select one can hold two ticks at once.
    private static SessionView _AskUserQuestionSession(bool answered) =>
        new() { DataContext = _BuildAskUserQuestionSession(answered) };

    // AC-722: the same question rows _AskUserQuestionSession renders in a session pane, built once here so the
    // assistant-chat scene below proves the merged TranscriptRowView rather than a second hand-tuned fixture.
    private static SessionViewModel _BuildAskUserQuestionSession(bool answered)
    {
        const string input = """
        {"questions":[
          {"question":"Which test suites should I run before the push?","header":"Tests","multiSelect":false,
           "options":[{"label":"Core only","description":"Fast, misses view regressions"},
                      {"label":"Everything","description":"Slower, but nothing slips through"}]},
          {"question":"What should I include in the commit message?","header":"Commit","multiSelect":true,
           "options":[{"label":"Ticket id"},{"label":"Test counts"},{"label":"Reviewer notes"}]}
        ]}
        """;

        var viewModel = new SessionViewModel { Title = "personal - webshop" };
        viewModel.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Before I push, two things I would rather not guess at." });
        viewModel.Apply(new ToolUseRequested { SessionId = "s1", ToolUseId = "toolu_q1", ToolName = "AskUserQuestion", InputJson = input });
        viewModel.Apply(new PermissionRequested { SessionId = "s1", ToolUseId = "toolu_q1", ToolName = "AskUserQuestion", InputJson = input });

        var entry = viewModel.Transcript.Single(row => row.ToolUseId == "toolu_q1");
        var prompts = entry.QuestionPrompts ?? [];
        if (answered)
        {
            prompts[0].Options[1].SelectCommand.Execute(null);
            prompts[1].Options[0].SelectCommand.Execute(null);
            prompts[1].Options[1].SelectCommand.Execute(null);
            foreach (var prompt in prompts)
            {
                prompt.IsAnswered = true;
            }

            entry.IsPendingPermission = false;
            entry.PermissionDecision = "Answered";
        }
        else
        {
            prompts[0].Options[0].SelectCommand.Execute(null);
        }

        return viewModel;
    }

    // Renders the full window with a plugin-update count seeded (AC-76) so the sidebar "Plugin store" button's
    // accent update badge is verifiable headless.
    private static MainWindow _PluginUpdateBadge()
    {
        var cockpit = new ViewModels.CockpitViewModel { GlobalSingleSessionLayout = true };
        cockpit.Plugins.SetUpdateBadgeCount(3);

        return new MainWindow { DataContext = cockpit };
    }

    // Wraps a plain UserControl (a wizard step has no Window of its own) in a Window carrying the same
    // DataContext, so a test can reach the view model straight off window.DataContext the way every other scene
    // here already lets it — rather than reaching through window.Content each time.
    private static Window _AsWindow(UserControl content, int width, int height) =>
        new() { Width = width, Height = height, Content = content, DataContext = content.DataContext };

    // AC-510[b]: one provider row per state the step distinguishes, staged directly the way _PluginStoreViewModel
    // stages its own catalogue — no store fetch, no PATH probe (Detection is passed in explicitly, so this scene
    // never depends on what happens to be installed on the machine that renders it).
    private static ViewModels.Onboarding.ProviderPickerRowViewModel _ProviderRow(
        string id, string name, string description, ViewModels.Onboarding.ProviderDetectionState detection,
        string? installedVersion = null, int hostAbstractionsMajor = Cockpit.Plugins.Abstractions.AbstractionsContract.Version)
    {
        var entry = new PluginStoreEntry(
            id, name, description, "Cockpit", "1.0.0",
            [new PluginStoreVersion("1.0.0", $"{id}-1.0.0.zip", Cockpit.Plugins.Abstractions.AbstractionsContract.Version, null, null, null)],
            Category: PluginStoreEntry.ProviderCategory);
        var store = PluginStoreConfig.Remote("https://raw.githubusercontent.com/raymondkrahwinkel/AI-Cockpit-Plugins/main/index.json");
        var row = new StorePluginRowViewModel(entry, store, installedVersion, hostAbstractionsMajor: hostAbstractionsMajor);

        return new ViewModels.Onboarding.ProviderPickerRowViewModel(row, detection);
    }

    private static Views.Onboarding.ProviderStepView _ProviderStepCatalogue()
    {
        var viewModel = new ViewModels.Onboarding.ProviderStepViewModel();
        viewModel.Providers.Add(_ProviderRow(
            "claude-provider", "Claude Code", "Requires the claude CLI installed and logged in on the machine running Cockpit.",
            ViewModels.Onboarding.ProviderDetectionState.Found));
        viewModel.Providers.Add(_ProviderRow(
            "cli-agent-provider", "Codex (ChatGPT)", "Requires the codex CLI installed and authenticated (codex login) on the machine running Cockpit.",
            ViewModels.Onboarding.ProviderDetectionState.NotFound));
        viewModel.Providers.Add(_ProviderRow(
            "gemini-provider", "Gemini / OpenAI Provider", "Configure an API key and model per profile in Manage profiles.",
            ViewModels.Onboarding.ProviderDetectionState.NotApplicable));
        viewModel.Providers.Add(_ProviderRow(
            "kimi-provider", "Kimi Code Provider (ACP)", "Requires the kimi CLI installed and authenticated on this machine.",
            ViewModels.Onboarding.ProviderDetectionState.Found, installedVersion: "0.2.0"));
        viewModel.Providers.Add(_ProviderRow(
            "github-models-provider", "GitHub Models", "Configure a GitHub personal access token (models:read scope) and model per profile.",
            ViewModels.Onboarding.ProviderDetectionState.NotApplicable, hostAbstractionsMajor: 999));

        return new Views.Onboarding.ProviderStepView { DataContext = viewModel };
    }

    private static Views.Onboarding.ProviderStepView _ProviderStepOffline()
    {
        var viewModel = new ViewModels.Onboarding.ProviderStepViewModel
        {
            IsOffline = true,
            OfflineMessage = "Could not fetch the store index: No such host is known.",
        };

        return new Views.Onboarding.ProviderStepView { DataContext = viewModel };
    }

    private static Views.Onboarding.ProviderStepView _ProviderStepInstallOutcomes()
    {
        var viewModel = new ViewModels.Onboarding.ProviderStepViewModel { SummaryMessage = "Installed 2 of 3 provider(s); Kimi Code Provider (ACP) didn't make it — see the reasons below." };

        var installed = _ProviderRow(
            "claude-provider", "Claude Code", "Requires the claude CLI installed and logged in on the machine running Cockpit.",
            ViewModels.Onboarding.ProviderDetectionState.Found);
        installed.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Installed, "claude-provider", "Claude Code", null, null, "claude-provider", "sha"));

        var staged = _ProviderRow(
            "cli-agent-provider", "Codex (ChatGPT)", "Requires the codex CLI installed and authenticated (codex login) on the machine running Cockpit.",
            ViewModels.Onboarding.ProviderDetectionState.NotFound, installedVersion: "0.5.2");
        staged.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Staged, "cli-agent-provider", "Codex (ChatGPT)", null, null, "cli-agent-provider", "sha"));

        var failed = _ProviderRow(
            "kimi-provider", "Kimi Code Provider (ACP)", "Requires the kimi CLI installed and authenticated on this machine.",
            ViewModels.Onboarding.ProviderDetectionState.Found);
        failed.ApplyOutcome(new PluginProvisionResult(PluginProvisionOutcome.Failed, "kimi-provider", "Kimi Code Provider (ACP)", "Could not download the plugin: the connection was reset.", null, null, null));

        viewModel.Providers.Add(installed);
        viewModel.Providers.Add(staged);
        viewModel.Providers.Add(failed);

        return new Views.Onboarding.ProviderStepView { DataContext = viewModel };
    }

    // Seed the plugin store directly so its full layout can be rendered headlessly without network access (#62).
    private static PluginStoreDialog _PluginStore() => new() { DataContext = _PluginStoreViewModel() };

    // NeedsRestart is on in both because that is the state that was reported — "Update all" raises it after the first
    // plugin of the batch, so the footer offers a restart while the rest are still downloading (AC-420).
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

    // AC-553: the real bundled-plugin ids/categories/LogoAsset (matching plugins-dev/*/store.json), so the
    // render exercises the actual converter/tint instead of the glyph/monogram fallbacks. No SelectedPlugin —
    // this scene is about the catalogue grid, not the detail panel "plugin-store" already covers.
    private static PluginStoreDialogViewModel _PluginStoreWithLogos()
    {
        static StorePluginRowViewModel Row(string id, string name, string category, string logoAsset, string icon)
        {
            var versions = new[] { new PluginStoreVersion("1.0.0", $"plugins/{id}.zip", null, null, null, null) };
            var entry = new PluginStoreEntry(id, name, null, "Cockpit", "1.0.0", versions, category, icon, LogoAsset: logoAsset);
            return new StorePluginRowViewModel(entry, PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"), null);
        }

        var manager = new PluginManagerViewModel();
        manager.Stores.Add(PluginStoreConfig.Remote("https://store.aicockpit.dev/index.json"));
        StorePluginRowViewModel[] rows =
        [
            Row("autopilot", "Autopilot", "Automation", "autopilot.svg", "🤖"),
            Row("depot", "Depot", "Project tools", "depot.svg", "🗄️"),
            Row("fan-out", "Fan-Out", "Automation", "fan-out.svg", "🍴"),
            Row("local-ci", "Local CI", "Automation", "local-ci.svg", "🧪"),
            Row("transcript-search", "Claude Transcript Search", "Productivity", "transcript-search.svg", "🔍"),
            Row("git-status", "Git status", "Productivity", "git-status.svg", "🌿"),
            Row("clock", "Clock", "Widgets", "clock.svg", "🕐"),
            Row("usage-trend", "Usage Trend", "Widgets", "usage-trend.svg", "📈"),
            // AC-553 option A: these three point at the vendor's own CDN — not fetched by this offline scene
            // (no network in a headless CI render), so they render on the glyph/monogram fallback here.
            Row("claude-provider", "Claude Code", "AI providers", "https://claude.ai/favicon.svg", null!),
            Row("cli-agent-provider", "Codex (ChatGPT)", "AI providers", "https://avatars.githubusercontent.com/openai", null!),
            Row("kimi-provider", "Kimi", "AI providers", "https://moonshotai.github.io/Branding-Guide/scenarios/04-k-only/k-only-color.svg", "🌙"),
            // No LogoAsset at all — criterion 3, the neutral-tile fallback, in the same grid as the tiles it must not
            // stand out from.
            Row("no-logo-yet", "Third-party sample", "Other", null!, "🧩"),
        ];

        foreach (var row in rows)
        {
            manager.AvailablePlugins.Add(row);
        }

        return new PluginStoreDialogViewModel(manager);
    }

    // Renders the agent-line inspector (AC-397) with one row in each of its five sections, including a refused send and
    // a claim old enough to look stale — the two rows an operator is actually scanning for, and the two a scene built
    // only from happy-path data would leave undrawn.
    private static ViewModels.CockpitViewModel _TwoSessionDesks()
    {
        var vm = new ViewModels.CockpitViewModel();
        var here = vm.Workspaces.Settings.Active!;
        var elsewhere = Cockpit.Core.Workspaces.Workspace.Create("Sessions 2", Cockpit.Core.Workspaces.WorkspaceType.Sessions);

        vm.Sessions[0].WorkspaceId = here.Id;
        vm.Sessions[1].WorkspaceId = here.Id;
        vm.Sessions[2].WorkspaceId = elsewhere.Id;

        vm.Workspaces.Settings = vm.Workspaces.Settings.WithWorkspace(elsewhere).WithActive(here.Id);
        return vm;
    }

    private static ViewModels.AgentLineInspectorViewModel _AgentLine()
    {
        var inspector = new ViewModels.AgentLineInspectorViewModel { DeskNote = "Desk ws-1 · 3 agent session(s)", EmptyNote = string.Empty };
        inspector.Messages.Add(new ViewModels.AgentLineMessageRow("09:12:03", "pane-a", "pane-b", "heads-up", "Accepted", "I am merging DEP-85 to dev — leave that branch alone for ten minutes."));
        inspector.Messages.Add(new ViewModels.AgentLineMessageRow("09:12:44", "pane-a", "pane-b", "heads-up", "RefusedRateLimited", "and again, and again, and again"));
        inspector.Wakes.Add(new ViewModels.AgentLineWakeRow("09:12:03", "pane-a", "pane-b", "Woken"));
        inspector.Wakes.Add(new ViewModels.AgentLineWakeRow("09:12:44", "pane-a", "pane-c", "AwaitingOperator"));
        inspector.Claims.Add(new ViewModels.AgentLineClaimRow("/home/raymond/RiderProjects/AI-Cockpit", "pane-b", "38 min"));
        inspector.Budget.Add(new ViewModels.AgentLineBudgetRow("pane-a", "Message", "20 of 20 in the last 60s"));
        inspector.Gaps.Add(new ViewModels.AgentLineGapRow("pane-c", "On this desk, but has never called a cockpit-agents tool. Either it has not looked yet, the server is not mounted for it, or its MCP injection failed silently."));
        return inspector;
    }

    // Seed representative private, public, and local stores to render every Manage-stores badge without I/O
    // (#62, AC-7).
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

    // The palette with commands in it, taken from the catalogue the real one is built from rather than written out here
    // — a made-up row renders a shortcut this app does not have, on a picture that looks like the app.
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

    // Every assistant chip state stacked at the width the sidebar really gives it, so they can be judged against each
    // other rather than one at a time in a window with room to spare.
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
            var indicator = new ViewModels.AssistantIndicatorViewModel
            {
                Activity = activity,
                UnavailableReason = reason,
                ListeningMode = activity == Cockpit.Core.Assistant.AssistantActivity.ListeningContinuously
                    ? Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn
                    : Cockpit.Core.Assistant.AssistantListeningMode.Off,
            };

            // A level, so the arc around the badge is on screen at all: it is driven by captured audio, and a
            // still render of a silent microphone would show the one part of this chip that moves as nothing.
            // The states without a microphone drop it themselves, so this needs no condition here.
            indicator.PushLevel(0.7);

            column.Children.Add(new Views.AssistantIndicator
            {
                Width = SidebarContentWidth,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                DataContext = indicator,
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

    // The chip is fed by AssistantIndicatorCoordinator at runtime; here it is set directly, for the reason the
    // component takes its state rather than fetching it — a scene that had to stand up a session host to draw a sidebar
    // would be testing the host (AC-543).
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

    // AC-543 (strand 3): builds the assistant indicator on a view model set directly to the state a scene name asks for
    // — the indicator is fed its state rather than owning it (AssistantIndicatorViewModel's own remarks on why it does
    // not bind to AssistantSessionHost), so a scene only ever has to set properties, never wire up a fake host.
    private static Window _AssistantIndicator(
        Cockpit.Core.Assistant.AssistantActivity activity,
        string? unavailableReason = null,
        Cockpit.Core.Assistant.AssistantListeningMode listeningMode = Cockpit.Core.Assistant.AssistantListeningMode.Off,
        bool collapsed = false,
        bool alwaysOnConfirmationPending = false,
        string? preparationStatus = null,
        double? preparationProgress = null)
    {
        var viewModel = new ViewModels.AssistantIndicatorViewModel
        {
            Activity = activity,
            UnavailableReason = unavailableReason,
            ListeningMode = listeningMode,
            IsCollapsed = collapsed,
            IsAlwaysOnConfirmationPending = alwaysOnConfirmationPending,
            PreparationStatus = preparationStatus,
            PreparationProgress = preparationProgress,
        };

        // See the all-states scene: without a level the arc has nothing to draw, and it is the moving part.
        viewModel.PushLevel(0.7);

        return new Window
        {
            Width = collapsed ? 120 : 340,
            Height = collapsed ? 120 : (alwaysOnConfirmationPending ? 320 : 220),
            Content = new Views.AssistantIndicator { DataContext = viewModel },
        };
    }

    // Use the established assistant-host seam so the chat scene needs no live CockpitViewModel (AC-543).
    private static AssistantChatWindow _AssistantChat(bool withConversation, bool speakReplies = true, bool alwaysOn = false)
    {
        var viewModel = _AssistantChatViewModel(
            withConversation ? new ViewModels.SessionViewModel() : null, speakReplies, alwaysOn);
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    private static ViewModels.AssistantChatViewModel _AssistantChatViewModel(
        ViewModels.SessionViewModel? session, bool speakReplies = true, bool alwaysOn = false)
    {
        var host = new _FakeAssistantSessionHost
        {
            Session = session,
            Activity = Cockpit.Core.Assistant.AssistantActivity.Ready,
        };

        // AC-662: the same Indicator the coordinator feeds the real window, so the header's always-on switch
        // renders here too — a scene fed no indicator at all would be testing a header this ticket's own switch
        // no longer draws.
        var indicator = new ViewModels.AssistantIndicatorViewModel
        {
            ListeningMode = alwaysOn
                ? Cockpit.Core.Assistant.AssistantListeningMode.AlwaysOn
                : Cockpit.Core.Assistant.AssistantListeningMode.Off,
        };

        return new ViewModels.AssistantChatViewModel(
            host, new _FakeAssistantSettingsStore(speakReplies), new _NullVoicePlaybackQueue(), indicator: indicator);
    }

    // AC-953: the assistant docked into the rail, built the way production builds it — the real `DockPanelRegistry`
    // with the real registration shape, and `AssistantDocked`/`OpenDockPanelId` set the way they come back off
    // `LayoutSettings` after a restart.
    private static Window _AssistantDockedInTheRail()
    {
        var conversation = new ViewModels.SessionViewModel { Title = "personal - webshop" };
        conversation.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.UserText, "which sessions are still running?"));
        conversation.Transcript.Add(new TranscriptEntryViewModel(
            TranscriptEntryKind.AssistantText, "Two: **personal - webshop** is waiting on a permission, and the AC-953 desk is idle."));

        var panels = new Docking.DockPanelRegistry();
        panels.Register(new Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration(
            Services.AssistantIndicatorCoordinator.DockPanelId,
            "Assistant",
            Material.Icons.MaterialIconKind.Creation,
            () =>
            {
                var chat = _AssistantChatViewModel(conversation);
                chat.IsDocked = true;
                return new AssistantChatView { DataContext = chat };
            }));

        var cockpit = new ViewModels.CockpitViewModel(dockPanelRegistry: panels)
        {
            OpenDockPanelId = Services.AssistantIndicatorCoordinator.DockPanelId,
            AssistantDocked = true,
        };

        return new MainWindow { DataContext = cockpit };
    }

    // AC-960: a plugin's own panel open in the rail, at a given content width. Stand-in rows rather than the
    // real GitHubPullRequestsWidget — see _PullRequestsDockPanel's own remarks.
    private static Window _DockRailWithPullRequestsPanel(double railWidth)
    {
        var panels = new Docking.DockPanelRegistry();
        panels.Register(_PullRequestsDockPanel());

        var cockpit = new ViewModels.CockpitViewModel(dockPanelRegistry: panels)
        {
            OpenDockPanelId = "github.pull-requests",
            DockRailWidth = railWidth,
        };

        return new MainWindow { DataContext = cockpit };
    }

    // AC-960: nothing open, so the rail is the 40px tab strip — with both the Assistant's tab (AC-953) and a
    // plugin's, proving the strip holds more than one without a real Assistant session behind it.
    private static Window _DockRailCollapsedWithTwoTabs()
    {
        var panels = new Docking.DockPanelRegistry();
        panels.Register(new Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration(
            Services.AssistantIndicatorCoordinator.DockPanelId, "Assistant", Material.Icons.MaterialIconKind.Creation, () => new TextBlock()));
        panels.Register(_PullRequestsDockPanel());

        var cockpit = new ViewModels.CockpitViewModel(dockPanelRegistry: panels);

        return new MainWindow { DataContext = cockpit };
    }

    // Cockpit.App has no project reference to Cockpit.Plugin.GitHubPullRequests — it is store-distributed, not bundled
    // — so this stands in for GitHubPullRequestsWidget, copying its own _BuildRow shape (number/title line, faint
    // repository line, amber left-border stripe for the one waiting on review) rather than plain text, verified
    private static Cockpit.Plugins.Abstractions.Docking.DockPanelRegistration _PullRequestsDockPanel() =>
        new("github.pull-requests", "Pull Requests", Material.Icons.MaterialIconKind.SourcePull, _BuildPullRequestsStandIn);

    private static Control _BuildPullRequestsStandIn()
    {
        (int Number, string Title, string Repository, bool Waiting)[] rows =
        [
            (101, "Faster startup path for the cold-start benchmark", "raymondkrahwinkel/cockpit", false),
            (202, "Dock rail: let a plugin register its own panel", "raymondkrahwinkel/cockpit", true),
            (203, "Fix flaky terminal-grid test on the CI runner", "raymondkrahwinkel/cockpit-plugins", false),
        ];

        var list = new StackPanel { Spacing = 1 };
        foreach (var row in rows)
        {
            list.Children.Add(_BuildStandInRow(row.Number, row.Title, row.Repository, row.Waiting));
        }

        return new DockPanel
        {
            Margin = new Thickness(4),
            Children =
            {
                new TextBlock
                {
                    [DockPanel.DockProperty] = Dock.Top,
                    Text = "3 open · 1 waiting on you",
                    FontSize = 11,
                    Foreground = _Brush("CockpitTextSecondaryBrush"),
                    Margin = new Thickness(2, 0, 0, 6),
                },
                new ScrollViewer { Content = list },
            },
        };
    }

    private static Control _BuildStandInRow(int number, string title, string repository, bool waiting)
    {
        var line = new DockPanel();
        var numberBlock = new TextBlock
        {
            [DockPanel.DockProperty] = Dock.Left,
            Text = $"#{number}",
            FontSize = 11,
            Foreground = waiting ? _Brush("CockpitStatusWaitingBrush") : _Brush("CockpitTextFaintBrush"),
            Margin = new Thickness(0, 0, 6, 0),
        };
        line.Children.Add(numberBlock);
        line.Children.Add(new TextBlock { Text = title, FontSize = 12, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis });

        var repositoryLine = new TextBlock
        {
            Text = waiting ? $"{repository} · waiting on your review" : repository,
            FontSize = 10,
            Foreground = _Brush("CockpitTextFaintBrush"),
        };

        return new Border
        {
            BorderThickness = new Thickness(2, 0, 0, 0),
            BorderBrush = waiting ? _Brush("CockpitStatusWaitingBrush") : Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(7, 5),
            Child = new StackPanel { Spacing = 1, Children = { line, repositoryLine } },
        };
    }

    private static Avalonia.Media.IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is Avalonia.Media.IBrush brush ? brush : null;

    // AC-740 addendum: no session yet, so this also proves the profile-default fallback renders the picker —
    // not just the session's own working directory, which the SessionView scene already covers.
    private static AssistantChatWindow _AssistantChatMentionPicker()
    {
        var host = new _FakeAssistantSessionHost { Session = null, DefaultWorkingDirectory = "/repo" };
        var viewModel = new ViewModels.AssistantChatViewModel(
            host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue(),
            mentionFileSource: new _SampleMentionFileSource());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // AC-683: the same stacked warnings _StackedWarningsSession renders in a session pane, on the assistant's
    // session instead — the window this ticket's criteria 1-3 add the pill row and the warning bar to, so the
    // scene proves both surfaces render the one SessionViewModel the same way rather than two hand-tuned copies.
    private static AssistantChatWindow _AssistantChatWithWarnings()
    {
        const long Cap = 512L * 1024 * 1024;
        // AC-893: without a capability that declares compaction, the Compact button never rendered in this
        // scene — the exact blind spot that let the missing wiring on the real surfaces go unnoticed.
        var session = new ViewModels.SessionViewModel
        {
            MemoryCapBytes = Cap,
            Capabilities = SessionCapabilities.ClaudeCli with { SupportsContextCompaction = true },
        };
        session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "Running the build." });
        session.ApplyUsage(
            [
                new PluginUsageSignal("context", "ctx", PluginUsageSignalKind.Fill, 50) { Description = "Context window" },
                new PluginUsageSignal("weekly", "wk", PluginUsageSignalKind.Allowance, 90) { Description = "Week" },
            ],
            [
                new PluginUsageReading("context", 55, null),
                new PluginUsageReading("weekly", 95, DateTimeOffset.Now.AddDays(3)),
            ]);
        session.ReportMemoryAgainstCap((long)(Cap * 1.12));

        var host = new _FakeAssistantSessionHost { Session = session, Activity = Cockpit.Core.Assistant.AssistantActivity.Ready };
        var viewModel = new ViewModels.AssistantChatViewModel(host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // AC-722: an unanswered AskUserQuestion in the pop-out, on the same session _BuildAskUserQuestionSession
    // builds for the session-pane scenes — the merge's own acceptance test (options/Other-fallback/multiSelect
    // rendering in the assistant chat, not just SessionView) gets a baseline of its own.
    private static AssistantChatWindow _AssistantChatQuestion()
    {
        var host = new _FakeAssistantSessionHost
        {
            Session = _BuildAskUserQuestionSession(answered: false),
            Activity = Cockpit.Core.Assistant.AssistantActivity.Ready,
        };

        var viewModel = new ViewModels.AssistantChatViewModel(host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // AC-1018: same card, built the way AssistantAgentGateway.AskStructuredQuestionAsync actually builds it
    // (Kind = Question, IsPendingBrokerAnswer = true) rather than through ToolUseRequested/PermissionRequested —
    // the route _BuildAskUserQuestionSession takes, and the one that never fails.
    private static AssistantChatWindow _AssistantChatBrokerQuestion()
    {
        const string input = """
        {"questions":[{"question":"Which profile should this run under?","header":"Profile","multiSelect":false,
          "options":[{"label":"Programmer (Opus)"},{"label":"Programmer (Sonnet)"}]}]}
        """;

        var session = new SessionViewModel { Title = "personal - webshop" };
        session.Apply(new AssistantTextDelta { SessionId = "s1", BlockIndex = 0, Text = "One thing before I start." });
        session.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.Question, "Which profile should this run under?")
        {
            InputJson = input,
            QuestionPrompts = AskUserQuestionViewModel.Parse(input),
            IsPendingBrokerAnswer = true,
        });

        var host = new _FakeAssistantSessionHost { Session = session, Activity = Cockpit.Core.Assistant.AssistantActivity.Ready };
        var viewModel = new ViewModels.AssistantChatViewModel(host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue());
        return new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
    }

    // AC-776: a real CockpitViewModel, not the usual fake host — the pill reads CockpitViewModel.Sessions
    // directly. `sessionCount`/`width` cover both the all-statuses scene and the narrow-wrap scene.
    private static AssistantChatWindow _AssistantChatSessionPill(int sessionCount, double? width = null)
    {
        var cockpit = new CockpitViewModel();
        cockpit.Sessions.Clear();

        SessionStatus[] statuses =
        [
            SessionStatus.Busy, SessionStatus.WaitingForInput, SessionStatus.NeedsAttention,
            SessionStatus.WorkingBackground, SessionStatus.Done, SessionStatus.Idle,
        ];
        string[] names = ["AC-774", "depot-fix", "ci-run", "AC-561-mockup-review", "cleanup-worktrees", "release-notes"];

        for (var i = 0; i < sessionCount; i++)
        {
            var session = new ViewModels.SessionViewModel { Title = names[i % names.Length] };
            session.AdoptPaneId($"scene-session-{i}");
            session.SessionStatus = statuses[i % statuses.Length];
            cockpit.Sessions.Add(session);
        }

        // A usage pill next to it (else there is nothing for the session pill to share a line with, and the
        // narrow-wrap scene would not actually wrap — see UsagePillUnreportedWindowTests' own remarks on why a
        // bare SessionViewModel reports no usage at all).
        var assistantSession = new ViewModels.SessionViewModel { UsagePillVisibleFields = [Cockpit.Core.UsagePill.UsagePillField.Context] };
        assistantSession.ContextUsedPercent = 37;
        var host = new _FakeAssistantSessionHost { Session = assistantSession, Activity = Cockpit.Core.Assistant.AssistantActivity.Ready };
        var viewModel = new ViewModels.AssistantChatViewModel(
            host, new _FakeAssistantSettingsStore(speakReplies: true), new _NullVoicePlaybackQueue(), cockpit: cockpit);
        var window = new AssistantChatWindow { DataContext = viewModel, Topmost = false, WindowStartupLocation = WindowStartupLocation.Manual };
        if (width is { } w)
        {
            window.Width = w;
        }

        return window;
    }

    // Bare-minimum IAssistantSessionHost: nothing mutates Session/Activity/UnavailableReason after a scene constructs
    // one, so PropertyChanged never actually has to fire — a single captured frame never sees a change.
    private sealed class _FakeAssistantSessionHost : ViewModels.IAssistantSessionHost
    {
        public ViewModels.SessionViewModel? Session { get; init; }

        public Cockpit.Core.Assistant.AssistantActivity Activity { get; init; }

        public string? UnavailableReason { get; init; }

        public string? DefaultWorkingDirectory { get; init; }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }

        public Task<ViewModels.SessionViewModel?> EnsureStartedAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

        // Same again: a still frame cannot show a restart, and a scene that tore its own session down would render
        // the empty state instead of the one its name promises.
        public Task<ViewModels.SessionViewModel?> RestartAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);

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

        public void ReportTranscribing(bool transcribing)
        {
        }

        public void ReportPreparing(string? status, double? fraction)
        {
        }
    }

    // Without it the assistant-profile scene renders a form for a provider that resolved to nothing: the label falls
    // back to Ollama, the session-defaults block has no options to show, and the environment-variables block is hidden
    // because `SupportsEnvVars` is a capability read off a registration.
    private sealed class _FakeClaudeProviderRegistry : Cockpit.Infrastructure.Sessions.IPluginProviderRegistry
    {
        private static readonly Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration Claude = new(
            ClaudePluginProfile.ProviderId,
            "Claude",
            _ => throw new NotSupportedException("A screenshot starts no session."),
            new Cockpit.Plugins.Abstractions.Sessions.PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true },
            _ => new _PlaceholderConfigView())
        {
            Options =
            [
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption(
                    "permission-mode", "Permission mode", ["default", "acceptEdits", "plan", "bypassPermissions"], "default")
                {
                    ChoiceLabels = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["default"] = "Ask permissions",
                        ["acceptEdits"] = "Accept edits",
                        ["plan"] = "Plan only",
                        ["bypassPermissions"] = "Bypass permissions",
                    },
                },
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption("model", "Model", ["opus", "sonnet", "haiku"], "sonnet"),
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption("effort", "Effort", ["low", "medium", "high"], "medium"),
            ],
        };

        public void Register(Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration registration) { }

        public IReadOnlyList<Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration> Registrations => [Claude];

        public Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration? Resolve(string providerId) =>
            providerId == ClaudePluginProfile.ProviderId ? Claude : null;
    }

    // Codex's four declared start options, in the shapes the editor renders them in: Sandbox and Approval carry
    // choices (combo), Model and Effort declare none (text box). Only Sandbox has a declared default, which since
    // AC-1102 no longer pre-fills anything — it is the New-session dialog's pre-selection, not the profile's.
    private sealed class _FakeCodexProviderRegistry : Cockpit.Infrastructure.Sessions.IPluginProviderRegistry
    {
        public const string ProviderId = "cli-agent-provider.codex";

        private static readonly Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration Codex = new(
            ProviderId,
            "Codex (CLI)",
            _ => throw new NotSupportedException("A screenshot starts no session."),
            new Cockpit.Plugins.Abstractions.Sessions.PluginSessionCapabilities(SupportsTools: true, SupportsPermissions: true) { SupportsEnvVars = true },
            _ => new _CodexPlaceholderConfigView())
        {
            Options =
            [
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption(
                    "sandbox", "Sandbox", ["read-only", "workspace-write", "danger-full-access"], "read-only"),
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption("model", "Model", Choices: []),
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption("effort", "Effort", Choices: []),
                new Cockpit.Plugins.Abstractions.Sessions.PluginSessionLaunchOption(
                    "approvalPolicy", "Approval", ["untrusted", "on-request", "never"]),
            ],
        };

        public void Register(Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration registration) { }

        public IReadOnlyList<Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration> Registrations => [Codex];

        public Cockpit.Plugins.Abstractions.Sessions.SessionProviderRegistration? Resolve(string providerId) =>
            providerId == ProviderId ? Codex : null;
    }

    private sealed class _CodexPlaceholderConfigView : Cockpit.Plugins.Abstractions.Sessions.IPluginProviderConfigView
    {
        public Control View { get; } = new TextBlock
        {
            Text = "The Codex plugin's own settings render here — command, working directory, managed CLI, API key.",
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        public bool TryGetConfigJson(out string configJson)
        {
            configJson = "{}";
            return true;
        }
    }

    private sealed class _PlaceholderConfigView : Cockpit.Plugins.Abstractions.Sessions.IPluginProviderConfigView
    {
        public Control View { get; } = new TextBlock
        {
            Text = "The Claude plugin's own settings render here — config directory, executable, managed CLI.",
            FontSize = 11,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };

        public bool TryGetConfigJson(out string configJson)
        {
            configJson = "{}";
            return true;
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
        public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
        {
        }

        public void Enqueue(IReadOnlyList<Cockpit.Core.Voice.SpeechSegment> segments, int speakerId, Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
        {
        }

        public void NotifyPreparing(Cockpit.Core.Voice.VoicePlaybackSource source = Cockpit.Core.Voice.VoicePlaybackSource.Session)
        {
        }

        public event EventHandler<bool>? PlaybackActiveChanged { add { } remove { } }

        public event EventHandler? SpeakingStarted { add { } remove { } }

        public void StopAll()
        {
        }

        public int Generation => 0;

        public Cockpit.Core.Voice.VoicePlaybackSource ActiveSource => Cockpit.Core.Voice.VoicePlaybackSource.Session;
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
            Row("codex-provider", "Codex (ChatGPT)", "Adds Codex CLI as a selectable session provider, driven as a subprocess per session.", "AI providers", "0.2.0", "🧩", featured: false, installed: false, homepage: true, repository: true),
            Row("gemini-openai", "Gemini / OpenAI Provider", "Adds Gemini and OpenAI models as selectable session providers, keyed per profile.", "AI providers", "0.4.0", "✨", featured: false, installed: true, hasSettings: true, homepage: true, repository: true),
        ];
    }

    private static AppBuilder BuildHeadlessAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            // The app's own font, for the same reason it takes the app's own fonts options: a render is only worth
            // looking at if it is of this program.
            .WithInterFont()
            .With(Program.CockpitFontOptions())
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });

    // The knowledge base staged over the app's own embedded documentation, with no plugin manager behind it —
    // the same index the running app builds, minus the plugins this build never loaded.
    private static Views.HelpWindow _Help(Core.Help.HelpAddress? address, string? arrivedFrom = null)
    {
        var help = new Services.HelpService([
            new Core.Help.HelpDocumentSource(Core.Help.HelpOwner.Core, typeof(Screenshotter).Assembly),
        ]);

        var window = new Views.HelpWindow(help);
        window.NavigateTo(address, arrivedFrom);

        return window;
    }

    private static Views.HelpWindow _HelpSearching(string query)
    {
        var window = _Help(null);
        window.SearchBox.Text = query;

        return window;
    }

}

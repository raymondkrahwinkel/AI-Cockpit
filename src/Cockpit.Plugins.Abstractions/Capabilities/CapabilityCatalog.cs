namespace Cockpit.Plugins.Abstractions.Capabilities;

/// <summary>
/// The fixed list of what a plugin can ask the host for: every public contribution point on
/// <see cref="ICockpitHost"/>, <see cref="ICockpitActions"/> and <see cref="IPluginStorage"/>, grouped into
/// the units a manifest declares and an operator grants.
/// </summary>
/// <remarks>
/// Host-only by construction (AC-474): the catalogue describes the host's own surface, so only the host can
/// add to it. Whether a plugin may ever contribute one waits on the trust boundary (AC-183).
/// </remarks>
public static class CapabilityCatalog
{
    // Ordered risk-first so "what does this plugin actually get" is answerable by reading down the list, and
    // so the generated table in docs/plugins/API-REFERENCE.md reads the same way.
    public static IReadOnlyList<PluginCapability> All { get; } =
    [
        new(
            "ui.settings",
            "Its own settings screen",
            "Adds the plugin's settings page behind the gear, and opens the cockpit's Options window.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddSettings", "ICockpitHost.ShowSettingsAsync", "ICockpitHost.HasSettings", "ICockpitHost.OnSettingsSaved"],
            []),

        new(
            "ui.side-menu",
            "A button in the left menu",
            "Adds the plugin's own launcher button or section, optionally with a live counter, to the left menu.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddSideMenuButton", "ICockpitHost.AddSideMenuSection", "ICockpitHost.AddSideMenuButtonWithBadge"],
            []),

        new(
            "ui.commands",
            "Toolbar buttons and keyboard shortcuts",
            "Adds invocable commands to the cockpit's toolbar and binds keyboard shortcuts to them.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddToolbarAction", "ICockpitHost.AddShortcut"],
            []),

        new(
            "ui.panels",
            "Panels on the dashboard and the dock rail",
            "Adds the plugin's own widgets to the dashboard, panels to the right-hand dock rail, and mini-tools to the companion window, and reads what is registered there.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddWidget", "ICockpitHost.Widgets", "ICockpitHost.AddDockPanel", "ICockpitHost.AddCompanionTool", "ICockpitHost.CompanionTools"],
            []),

        new(
            "ui.session-chrome",
            "Controls around a session",
            "Adds the plugin's own header items, banners, actions and conversation picker around a running session.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddSessionHeaderItem", "ICockpitHost.AddSessionBanner", "ICockpitHost.AddSessionHeaderAction", "ICockpitHost.AddConversationPicker"],
            []),

        new(
            "ui.status-bar",
            "A line in the status bar",
            "Reports the plugin's own long-running work in the cockpit's supervised-activity status bar.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddSupervisedActivityProvider"],
            []),

        new(
            "ui.dialogs",
            "Windows, toasts and confirmations",
            "Puts the plugin's own window, toast or confirmation in front of the operator, in the cockpit's own chrome.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.ShowDialogAsync", "ICockpitHost.ShowToast", "ICockpitActions.ConfirmAsync"],
            []),

        new(
            "ui.host-views",
            "Host-rendered read-only views",
            "Renders markdown and help hints through the cockpit's own controls instead of bundling a second renderer.",
            CapabilityRisk.Ambient,
            "0.7.0",
            ["ICockpitHost.CreateMarkdownView", "ICockpitHost.CreateHelpHint", "ICockpitHost.OpenHelp", "ICockpitHost.HasHelp"],
            []),

        new(
            "consent.request",
            "Asking the operator to approve an action",
            "Puts an Approve/Deny question to the operator through the cockpit's own consent surface.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.RequestConsentAsync"],
            []),

        new(
            "storage.settings",
            "Its own settings storage",
            "Reads and writes the plugin's own key/value section of the cockpit's configuration. No other plugin's section is reachable.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["IPluginStorage.Get", "IPluginStorage.Set"],
            []),

        new(
            "workspaces.types",
            "Its own kind of workspace",
            "Registers a workspace type the operator can open, and reads the types other plugins registered.",
            CapabilityRisk.Ambient,
            "0.3.0",
            ["ICockpitHost.AddWorkspaceType", "ICockpitHost.WorkspaceTypes", "ICockpitHost.OpenWorkspaceAsync"],
            []),

        new(
            "storage.secrets",
            "Storing credentials",
            "Writes and reads credentials in the cockpit's secret store — encrypted at rest and emptied from a credential-free backup.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["IPluginStorage.SetSecret", "IPluginStorage.GetSecret"],
            [new("key", "The storage key the credential is filed under.")]),

        new(
            "clipboard.write",
            "Writing the clipboard",
            "Replaces what the operator has on the system clipboard, which every other application on the machine can read.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitActions.SetClipboardTextAsync"],
            []),

        new(
            "plugins.inventory",
            "Listing the installed plugins",
            "Reads which plugins are installed on this machine, and their versions.",
            CapabilityRisk.Sensitive,
            "0.5.0",
            ["ICockpitHost.InstalledPlugins"],
            []),

        new(
            "profiles.read",
            "Reading the configured profiles",
            "Reads the operator's profiles — which providers, models and permission ceilings are set up.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.GetProfilesAsync"],
            []),

        new(
            "sessions.observe",
            "Watching the running sessions",
            "Reads which sessions are open, what they are doing, and which pane an MCP call arrived from.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.Sessions", "ICockpitHost.CurrentMcpCallerPaneId"],
            [new("paneId", "The session pane the plugin may observe.")]),

        new(
            "sessions.annotate",
            "Naming a session",
            "Sets the statusline and the name of a session — what it says it is doing, and what it is called.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.SetSessionStatusline", "ICockpitHost.SetSessionName", "ICockpitHost.SuggestSessionName", "ICockpitActions.SetActiveSessionStatusAsync"],
            [new("paneId", "The session pane the plugin may rename.")]),

        new(
            "sessions.compose",
            "Proposing a new session",
            "Opens the New-session dialog with a prompt and a project filled in. The operator still confirms it.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.ShowNewSessionDialogAsync"],
            []),

        new(
            "workflows.steps",
            "Steps and templates for workflows",
            "Contributes steps and templates the operator can put in a workflow, and reads the ones already there. A contributed step runs unattended once a workflow does.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.AddWorkflowStep", "ICockpitHost.WorkflowSteps", "ICockpitHost.AddWorkflowTemplate", "ICockpitHost.WorkflowTemplates"],
            []),

        new(
            "workflows.trigger-observe",
            "Watching workflow triggers",
            "Sees every workflow trigger raised in the cockpit, including the data other plugins put on it.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.WorkflowTriggerRaised"],
            [new("typeId", "The trigger type the plugin may watch.")]),

        new(
            "autopilot.templates",
            "Autopilot templates",
            "Registers autopilot run templates the operator can pick, and reads the ones already registered.",
            CapabilityRisk.Sensitive,
            "0.5.0",
            ["ICockpitHost.RegisterAutopilotTemplate", "ICockpitHost.RegisteredAutopilotTemplates"],
            []),

        new(
            "projects.fields",
            "Fields on a project",
            "Adds fields to the project editor and claims ownership of host fields, so the plugin decides what they hold.",
            CapabilityRisk.Sensitive,
            "0.7.0",
            ["ICockpitHost.AddProjectField", "ICockpitHost.ProjectFields", "ICockpitHost.ClaimProjectOwnership", "ICockpitHost.GetProjectFieldOwnership"],
            []),

        new(
            "projects.read",
            "Reading project field values",
            "Reads what the operator filled in on a project — repository paths, tracker keys, whatever other plugins put there.",
            CapabilityRisk.Sensitive,
            "0.7.0",
            ["ICockpitHost.GetProjectFieldValueAsync", "ICockpitHost.GetProjectFieldValuesAsync"],
            [new("key", "The project field key the plugin may read.")]),

        new(
            "projects.memory-source",
            "Offering a project memory source",
            "Registers where a project's memory can live and is asked to serve it, so a starting session is pointed at the plugin.",
            CapabilityRisk.Sensitive,
            "0.10.0",
            ["ICockpitHost.AddProjectMemorySource", "ICockpitHost.RemoveProjectMemorySource", "ICockpitHost.ProjectMemorySources", "ICockpitHost.AddProjectMemorySourceFamily"],
            [new("scheme", "The memory-source scheme the plugin may register under.")]),

        new(
            "projects.memory-read",
            "Reading project memory",
            "Reads a project's memory rows — the notes and instructions a session is started with.",
            CapabilityRisk.Sensitive,
            "0.22.0",
            ["ICockpitHost.GetProjectMemoryRowsAsync"],
            []),

        new(
            "projects.shared-source",
            "Offering shared projects",
            "Registers a source of shared projects the operator can pull in and bind to a local one.",
            CapabilityRisk.Sensitive,
            "0.19.0",
            ["ICockpitHost.AddSharedProjectSource", "ICockpitHost.RemoveSharedProjectSource", "ICockpitHost.SharedProjectSources"],
            [new("key", "The shared-project source key the plugin may register under.")]),

        new(
            "tracking.providers",
            "Being an issue tracker",
            "Serves the cockpit's issue, comment and stage lookups, so ticket content and the credentials behind it run through the plugin.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.AddTrackerProvider", "ICockpitHost.TrackerProviders"],
            []),

        new(
            "workspaces.git",
            "Reading and preparing git working copies",
            "Probes a directory for a git repository and creates isolated worktrees in it — a write to the operator's own checkout.",
            CapabilityRisk.Sensitive,
            "0.3.0",
            ["ICockpitHost.CreateRunWorktreeAsync", "ICockpitHost.DetectGitDirectoryStatusAsync"],
            [new("directory", "The repository directory the plugin may work in.")]),

        new(
            "workspaces.paths",
            "The remembered working directories",
            "Reads and adds to the shared history of directories the operator has worked in, which describes their filesystem.",
            CapabilityRisk.Sensitive,
            "0.4.0",
            ["ICockpitHost.GetRememberedWorkingPathsAsync", "ICockpitHost.RememberWorkingPathAsync"],
            []),

        new(
            "host.services",
            "The host's service provider",
            "Resolves the cockpit's own internal services directly, which is every other capability at once and none of them named.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.Services"],
            []),

        new(
            "plugins.intents",
            "Calling other plugins",
            "Handles and sends plugin-to-plugin intents. Sending one reaches whatever the target plugin was granted, so it is a route around this plugin's own grants.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.RegisterIntentHandler", "ICockpitHost.SendIntent", "ICockpitHost.CanSendIntent"],
            [new("targetPluginId", "The plugin whose intents may be called.")]),

        new(
            "workflows.trigger-raise",
            "Starting a workflow",
            "Raises a workflow trigger, which starts whatever the operator wired to it — unattended, with their rights.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.RaiseWorkflowTrigger"],
            [new("typeId", "The trigger type the plugin may raise.")]),

        new(
            "sessions.start",
            "Starting sessions",
            "Opens a session on a named profile with a first prompt, running an agent with the operator's rights.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitActions.StartSessionAsync"],
            [new("profileLabel", "The profile the plugin may start a session on.")]),

        new(
            "sessions.delegate",
            "Handing work to a profile",
            "Runs work on another profile as a background task and waits for the result, up to that profile's own permission ceiling.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitActions.DelegateAsync"],
            [
                new("profileLabel", "The profile the plugin may delegate to."),
                new("permission", "The highest permission mode the plugin may ask for (e.g. acceptEdits)."),
            ]),

        new(
            "sessions.drive",
            "Typing into a running session",
            "Sends text into a session's input — the operator's own keyboard, aimed at an agent that is already running.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.SendToSessionAsync", "ICockpitHost.BindToSession", "ICockpitActions.InjectIntoActiveSessionAsync", "ICockpitActions.HasActiveSession"],
            [new("paneId", "The session pane the plugin may type into.")]),

        new(
            "sessions.provide",
            "Being a session provider",
            "Runs the agent behind a session kind, so every prompt, response and credential of those sessions passes through the plugin.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.AddSessionProvider", "ICockpitHost.AddTtyProvider"],
            []),

        new(
            "sessions.resources",
            "Putting content into a session's context",
            "Contributes the files and resources a session is started with, which is text an agent will act on.",
            CapabilityRisk.Dangerous,
            "0.7.0",
            ["ICockpitHost.AddSessionResourceProvider", "ICockpitHost.SessionResourceProviders"],
            []),

        new(
            "mcp.contribute",
            "Adding MCP servers",
            "Registers MCP servers the cockpit's own sessions will call, and drives their sign-in.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.AddMcpServer", "ICockpitHost.RemoveMcpServer", "ICockpitHost.GetMcpServerAuthStateAsync", "ICockpitHost.SignInMcpServerAsync"],
            [new("serverName", "The MCP server the plugin may register or sign in.")]),

        new(
            "mcp.call",
            "Calling MCP tools",
            "Calls and probes MCP tools through the host, under whatever credentials the host holds for that server.",
            CapabilityRisk.Dangerous,
            "0.14.0",
            ["ICockpitHost.CallMcpToolAsync", "ICockpitHost.ProbeMcpToolAsync"],
            [
                new("serverName", "The MCP server the plugin may call."),
                new("toolName", "The tool on that server the plugin may call."),
            ]),

        new(
            "mcp.expose",
            "Serving its own MCP tools",
            "Publishes the plugin's own tools as an MCP endpoint, so they land in the tool loop of every session that uses it.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            ["ICockpitHost.AddMcpEndpoint"],
            [new("serverName", "The endpoint name the plugin may publish under.")]),

        new(
            "cli.managed",
            "Installing and running a managed CLI",
            "Downloads an executable from the URL the plugin names, installs it, keeps it updated and resolves its path for running.",
            CapabilityRisk.Dangerous,
            "0.3.0",
            [
                "ICockpitHost.AddManagedCli",
                "ICockpitHost.ResolveManagedCliPath",
                "ICockpitHost.InstallManagedCliAsync",
                "ICockpitHost.RemoveManagedCli",
                "ICockpitHost.GetManagedCliStatusAsync",
                "ICockpitHost.GetManagedCliAutoUpdateAsync",
                "ICockpitHost.SetManagedCliAutoUpdateAsync",
            ],
            [new("cliName", "The managed CLI the plugin may install and resolve.")]),

        new(
            "channels.assistant",
            "A chat channel onto the assistant",
            "Runs a Discord or Slack bot as a second door onto the assistant's standing conversation — inbound instructions and outbound egress both.",
            CapabilityRisk.Dangerous,
            "0.27.0",
            ["ICockpitHost.OpenAssistantChannel"],
            []),
    ];
}

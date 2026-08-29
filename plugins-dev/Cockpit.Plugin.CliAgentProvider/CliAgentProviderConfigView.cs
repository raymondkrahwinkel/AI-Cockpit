using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// The "add/edit profile" config panel for the Codex CLI provider (#45 fase B1): the CLI command/path, the
// working directory (also the sandbox root), and an optional API key. AC-1102: sandbox and model are asked
// once, under SESSION DEFAULTS, so this panel carries their stored pair as a fallback but never edits it.
internal sealed class CliAgentProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _command;
    private readonly TextBox _workingDirectory;
    private readonly TextBox _apiKey;
    private readonly TextBlock _commandStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _workingDirectoryStatus = ProviderConfigStatus.CreateLine();

    private readonly ICockpitHost _host;
    private readonly ManagedCliConfigSection _managedCli;
    private readonly CliAgentConfig? _existing;

    public Control View { get; }

    public CliAgentProviderConfigView(string? existingConfigJson, ICockpitHost host)
    {
        _host = host;
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<CliAgentConfig>(existingConfigJson, CliAgentConfig.JsonOptions);
        _existing = existing;
        // The panel refreshes the command-status line after install/remove, so the two never disagree.
        _managedCli = new ManagedCliConfigSection(host, CodexManagedCli.CliName, "Codex CLI", _UpdateCommandStatus);

        _command = new TextBox { Text = existing?.Command ?? "codex" };
        _workingDirectory = new TextBox { Text = existing?.WorkingDirectory ?? string.Empty, PlaceholderText = "Directory codex may read (and, in workspace-write, edit)" };

        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _LabelRow("Codex command / path", host.CreateHelpHint("setup", "codex-command")),
                _command,
                _commandStatus,
                _managedCli.View,
                _LabelRow("Working directory (optional — SDK sessions only)", host.CreateHelpHint("setup", "working-directory")),
                _workingDirectory,
                _workingDirectoryStatus,
                _LabelRow("API key (optional)", host.CreateHelpHint("setup", "api-key")),
                _apiKey,
            },
        };

        // Live per-field feedback (#45): auto-detect the executable on PATH so the operator sees at once whether the
        // command resolves (and where), and flag a working directory that does not exist — the two things that
        // silently make a profile unusable otherwise.
        _command.TextChanged += (_, _) => _UpdateCommandStatus();
        _workingDirectory.TextChanged += (_, _) => _UpdateWorkingDirectoryStatus();
        _UpdateCommandStatus();
        _UpdateWorkingDirectoryStatus();
    }

    // Resolves the command exactly as a session spawn will (pin &gt; managed &gt; PATH) and states, in one line, what
    // will run and whether it is a cockpit-managed copy — so this never contradicts the managed panel below.
    private void _UpdateCommandStatus()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_commandStatus, "Required — enter \"codex\" or an absolute path to the executable.", isOk: false);
            return;
        }

        var isPinned = Path.IsPathRooted(command);
        var resolved = CliExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
        var managedPath = _host.ResolveManagedCliPath(CodexManagedCli.CliName);

        if (!isPinned && !string.IsNullOrEmpty(managedPath) && string.Equals(resolved, managedPath, StringComparison.Ordinal))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Managed by Cockpit — this copy is used: {resolved}", isOk: true);
        }
        else if (isPinned && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Using pinned path (not managed): {resolved}", isOk: true);
        }
        else if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Found on PATH (not managed): {resolved}", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_commandStatus, "Not found on PATH — install it below, or paste an absolute path.", isOk: false);
        }
    }

    // Flags a non-empty working directory that does not exist (the one thing that blocks saving besides an empty command); an empty value is fine (SDK sessions fall back to the cockpit's own directory).
    private void _UpdateWorkingDirectoryStatus()
    {
        var directory = _workingDirectory.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(directory))
        {
            _workingDirectoryStatus.IsVisible = false;
            return;
        }

        _workingDirectoryStatus.IsVisible = true;
        if (Directory.Exists(directory))
        {
            ProviderConfigStatus.Set(_workingDirectoryStatus, "Folder found.", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_workingDirectoryStatus, "Folder does not exist — the profile cannot be saved until it does.", isOk: false);
        }
    }

    public bool TryGetConfigJson(out string configJson)
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        var workingDirectory = _workingDirectory.Text?.Trim() ?? string.Empty;

        // Optional and only read by the headless route: a TTY session gets its working directory from the
        // New-session dialog, but the plugin session-driver contract carries none, so headless has to ask for
        // it here. A contract gap, not a real setting — closing it would let this field go entirely.
        if (string.IsNullOrEmpty(command) || (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory)))
        {
            configJson = string.Empty;
            return false;
        }

        // Carried over untouched rather than re-defaulted: a profile still running on this pair as its fallback
        // would otherwise be reset to "read-only" by a save that touched an entirely different field.
        var config = new CliAgentConfig(
            Command: command,
            WorkingDirectory: workingDirectory,
            SandboxMode: _existing?.SandboxMode ?? "read-only",
            Model: _existing?.Model,
            ApiKey: string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim());

        configJson = JsonSerializer.Serialize(config, CliAgentConfig.JsonOptions);
        return true;
    }

    // AC-1043: a label with the SDK-drawn "?" beside it, pointing at the section of this plugin's own setup
    // page that explains the field below — replaces the old `SettingsHelpRow` hover tooltip.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 4, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };
}

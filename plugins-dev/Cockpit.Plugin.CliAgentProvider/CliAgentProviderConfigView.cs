using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

// The "add/edit profile" config panel for the Codex CLI provider (#45 fase B1): the CLI command/path, the
// working directory (also the sandbox root), the sandbox mode, an optional model override, and an optional
// API key — mirroring `OpenAiCompatProviderConfigView`'s shape from the Gemini/OpenAI provider plugin.
internal sealed class CliAgentProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _command;
    private readonly TextBox _workingDirectory;
    private readonly ComboBox _sandboxMode;
    private readonly AutoCompleteBox _model;
    private readonly TextBox _apiKey;
    private readonly TextBlock _commandStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _workingDirectoryStatus = ProviderConfigStatus.CreateLine();

    private readonly ICockpitHost _host;
    private readonly ManagedCliConfigSection _managedCli;

    public Control View { get; }

    public CliAgentProviderConfigView(string? existingConfigJson, ICockpitHost host)
    {
        _host = host;
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<CliAgentConfig>(existingConfigJson, CliAgentConfig.JsonOptions);
        // The panel refreshes the command-status line after install/remove, so the two never disagree.
        _managedCli = new ManagedCliConfigSection(host, CodexManagedCli.CliName, "Codex CLI", _UpdateCommandStatus);

        _command = new TextBox { Text = existing?.Command ?? "codex" };
        _workingDirectory = new TextBox { Text = existing?.WorkingDirectory ?? string.Empty, PlaceholderText = "Directory codex may read (and, in workspace-write, edit)" };

        _sandboxMode = new ComboBox { ItemsSource = CodexSandbox.Choices, SelectedItem = existing?.SandboxMode ?? "read-only" };

        // Free text with live suggestions, not a hard dropdown: a profile default may still pin any model (or one
        // this machine cannot list right now, e.g. logged out) — an AutoCompleteBox is both, a plain ComboBox
        // would be only the list. The suggestions are filled in the background from this codex's model/list
        // (increment 2 step C, the config-view mirror of the New-session dialog's Model dropdown).
        _model = new AutoCompleteBox
        {
            Text = existing?.Model ?? string.Empty,
            PlaceholderText = "e.g. gpt-5-codex (blank = codex's own default)",
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
        };
        _ = _PopulateModelSuggestionsAsync(existing?.Command ?? "codex", existing?.ConfigDir);

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
                _LabelRow("Sandbox mode", host.CreateHelpHint("setup", "sandbox-mode")),
                _sandboxMode,
                _Label("Model (optional)"),
                _model,
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

        // The working directory is optional, and only the headless route reads it at all: a TTY session is told
        // where it runs by the cockpit (the New-session dialog's own working directory), so demanding it here
        // would make the operator fill in a field their session never uses.
        //
        // The headless route needs it because the plugin session-driver contract does not carry a working
        // directory — the host resolves one and the adapter drops it. So the plugin has to ask for what the
        // cockpit already knows. That is a gap in the contract, not a setting; when it is closed this field can
        // go entirely.
        if (string.IsNullOrEmpty(command) || (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory)))
        {
            configJson = string.Empty;
            return false;
        }

        var config = new CliAgentConfig(
            Command: command,
            WorkingDirectory: workingDirectory,
            SandboxMode: _sandboxMode.SelectedItem as string ?? "read-only",
            Model: string.IsNullOrWhiteSpace(_model.Text) ? null : _model.Text.Trim(),
            ApiKey: string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim());

        configJson = JsonSerializer.Serialize(config, CliAgentConfig.JsonOptions);
        return true;
    }

    // Fills the Model field's suggestions from the models this codex offers (`model/list`), best-effort:
    // no codex, logged out, or a slow spawn just leaves it free text. Uses the profile's own command and
    // CODEX_HOME so a per-profile install/login lists its own models.
    private async Task _PopulateModelSuggestionsAsync(string command, string? configDir)
    {
        try
        {
            var config = new CliAgentConfig(Command: command, ConfigDir: configDir);
            var executablePath = CliExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var listing = await CodexModelCatalog.ListAsync(() => new ProcessCliSubprocess(), config, executablePath, cts.Token).ConfigureAwait(true);
            if (listing.Ids.Count > 0)
            {
                _model.ItemsSource = listing.Ids;
            }
        }
        catch (Exception)
        {
            // No suggestions — the field stays free text, which is a perfectly good way to set a model.
        }
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    // AC-1043: a label with the SDK-drawn "?" beside it, pointing at the section of this plugin's own setup
    // page that explains the field below — replaces the old `SettingsHelpRow` hover tooltip.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 4, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };
}

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// The "add/edit profile" config panel for the Codex CLI provider (#45 fase B1): the CLI command/path, the
/// working directory (also the sandbox root), the sandbox mode, an optional model override, and an optional
/// API key — mirroring <c>OpenAiCompatProviderConfigView</c>'s shape from the Gemini/OpenAI provider plugin.
/// </summary>
internal sealed class CliAgentProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _command;
    private readonly TextBox _workingDirectory;
    private readonly ComboBox _sandboxMode;
    private readonly AutoCompleteBox _model;
    private readonly TextBox _apiKey;
    private readonly TextBlock _commandStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _workingDirectoryStatus = ProviderConfigStatus.CreateLine();

    public Control View { get; }

    public CliAgentProviderConfigView(string? existingConfigJson)
    {
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<CliAgentConfig>(existingConfigJson, CliAgentConfig.JsonOptions);

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
                _Label("Codex command / path"),
                SettingsHelpRow.Build(_command, "Bare \"codex\" is resolved against PATH (including a Windows .cmd npm shim); or paste an absolute path to the executable."),
                _commandStatus,
                _Label("Working directory (optional — SDK sessions only)"),
                SettingsHelpRow.Build(_workingDirectory, "A TTY session runs where the New-session dialog says, so it ignores this. An SDK session cannot be told where it runs — the plugin contract carries no working directory — so it uses this, and falls back to the cockpit's own directory when it is empty."),
                _workingDirectoryStatus,
                _Label("Sandbox mode"),
                SettingsHelpRow.Build(_sandboxMode, "read-only is Codex's safe default. workspace-write allows edits inside the working directory; danger-full-access runs Codex with no sandboxing at all — only on a machine/workdir you fully trust."),
                _Label("Model (optional)"),
                _model,
                _Label("API key (optional)"),
                SettingsHelpRow.Build(_apiKey, "Only needed if this machine is not already logged in via \"codex login\". Set via CODEX_API_KEY for this spawn only — never passed as a CLI argument, never logged."),
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

    /// <summary>Resolves the command against PATH (cross-OS, incl. Windows .cmd/.exe/.bat shims) and shows where it was found, or that it is not on PATH — informational, since a profile may legitimately pin a command for a machine that has it installed elsewhere.</summary>
    private void _UpdateCommandStatus()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_commandStatus, "Required — enter \"codex\" or an absolute path to the executable.", isOk: false);
            return;
        }

        var resolved = CliExecutableLocator.Resolve(command);
        if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Found: {resolved}", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_commandStatus, "Not found on PATH — check it is installed, or paste an absolute path.", isOk: false);
        }
    }

    /// <summary>Flags a non-empty working directory that does not exist (the one thing that blocks saving besides an empty command); an empty value is fine (SDK sessions fall back to the cockpit's own directory).</summary>
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

    /// <summary>
    /// Fills the Model field's suggestions from the models this codex offers (<c>model/list</c>), best-effort:
    /// no codex, logged out, or a slow spawn just leaves it free text. Uses the profile's own command and
    /// CODEX_HOME so a per-profile install/login lists its own models.
    /// </summary>
    private async Task _PopulateModelSuggestionsAsync(string command, string? configDir)
    {
        try
        {
            var config = new CliAgentConfig(Command: command, ConfigDir: configDir);
            var executablePath = CliExecutableLocator.Resolve(command);
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
}

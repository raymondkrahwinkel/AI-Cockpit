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
    private readonly TextBox _model;
    private readonly TextBox _apiKey;

    public Control View { get; }

    public CliAgentProviderConfigView(string? existingConfigJson)
    {
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<CliAgentConfig>(existingConfigJson, CliAgentConfig.JsonOptions);

        _command = new TextBox { Text = existing?.Command ?? "codex" };
        _workingDirectory = new TextBox { Text = existing?.WorkingDirectory ?? string.Empty, PlaceholderText = "Directory codex may read (and, in workspace-write, edit)" };

        var sandboxModes = new[] { "read-only", "workspace-write", "danger-full-access" };
        _sandboxMode = new ComboBox { ItemsSource = sandboxModes, SelectedItem = existing?.SandboxMode ?? "read-only" };

        _model = new TextBox { Text = existing?.Model ?? string.Empty, PlaceholderText = "e.g. gpt-5-codex (blank = codex's own default)" };
        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("Codex command / path"),
                SettingsHelpRow.Build(_command, "Bare \"codex\" is resolved against PATH (including a Windows .cmd npm shim); or paste an absolute path to the executable."),
                _Label("Working directory"),
                _workingDirectory,
                _Label("Sandbox mode"),
                SettingsHelpRow.Build(_sandboxMode, "read-only is Codex's safe default. workspace-write allows edits inside the working directory; danger-full-access runs Codex with no sandboxing at all — only on a machine/workdir you fully trust."),
                _Label("Model (optional)"),
                _model,
                _Label("API key (optional)"),
                SettingsHelpRow.Build(_apiKey, "Only needed if this machine is not already logged in via \"codex login\". Set via CODEX_API_KEY for this spawn only — never passed as a CLI argument, never logged."),
            },
        };
    }

    public bool TryGetConfigJson(out string configJson)
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        var workingDirectory = _workingDirectory.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(command) || string.IsNullOrEmpty(workingDirectory) || !Directory.Exists(workingDirectory))
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

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
}

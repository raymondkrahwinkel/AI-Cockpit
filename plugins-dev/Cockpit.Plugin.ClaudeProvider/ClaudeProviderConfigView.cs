using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.ClaudeProvider;

/// <summary>
/// The "add/edit profile" config panel for the Claude provider (Fase 4, SDK route): the two fields the host's in-tree
/// <c>ClaudeConfig</c> carried — an optional config directory (which login/config this profile reads) and an optional
/// executable path. Both blank means a default session against the machine's own <c>claude</c> login, which is the
/// common case, so nothing is required.
/// </summary>
internal sealed class ClaudeProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _configDir;
    private readonly TextBox _executablePath;

    public Control View { get; }

    public ClaudeProviderConfigView(string? existingConfigJson)
    {
        var existing = ClaudeProviderConfig.Parse(existingConfigJson);

        _configDir = new TextBox
        {
            Text = existing.ConfigDir ?? string.Empty,
            PlaceholderText = "Blank = the machine's own ~/.claude login",
        };
        _executablePath = new TextBox
        {
            Text = existing.ExecutablePath ?? string.Empty,
            PlaceholderText = "Blank = \"claude\" resolved against PATH",
        };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("Config directory (optional)"),
                _configDir,
                _Label("Claude executable / path (optional)"),
                _executablePath,
            },
        };
    }

    public bool TryGetConfigJson(out string configJson)
    {
        var configDir = _configDir.Text?.Trim();
        var executablePath = _executablePath.Text?.Trim();

        // A given config directory must exist — a typo here silently sends the CLI to an empty config and a
        // logged-out session, so it is the one thing worth validating. Both fields blank is valid (a default session).
        if (!string.IsNullOrEmpty(configDir) && !Directory.Exists(configDir))
        {
            configJson = string.Empty;
            return false;
        }

        var config = new ClaudeProviderConfig(
            ConfigDir: string.IsNullOrWhiteSpace(configDir) ? null : configDir,
            ExecutablePath: string.IsNullOrWhiteSpace(executablePath) ? null : executablePath);

        configJson = JsonSerializer.Serialize(config, ClaudeProviderConfig.JsonOptions);
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
}

using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.ManagedCli;
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
    private readonly TextBlock _configDirStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _executableStatus = ProviderConfigStatus.CreateLine();

    private readonly ManagedCliConfigSection _managedCli;

    public Control View { get; }

    public ClaudeProviderConfigView(string? existingConfigJson, ICockpitHost host)
    {
        var existing = ClaudeProviderConfig.Parse(existingConfigJson);
        _managedCli = new ManagedCliConfigSection(host, ClaudeManagedCli.CliName, "Claude CLI");

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
                _configDirStatus,
                _Label("Claude executable / path (optional)"),
                _executablePath,
                _executableStatus,
                _managedCli.View,
            },
        };

        // Live per-field feedback (#45): auto-detect the claude executable on PATH and flag a config directory that
        // does not exist — the two things that silently make a profile unusable.
        _configDir.TextChanged += (_, _) => _UpdateConfigDirStatus();
        _executablePath.TextChanged += (_, _) => _UpdateExecutableStatus();
        _UpdateConfigDirStatus();
        _UpdateExecutableStatus();
    }

    /// <summary>Flags a non-empty config directory that does not exist (the one field that blocks saving); blank is fine (the machine's own ~/.claude login).</summary>
    private void _UpdateConfigDirStatus()
    {
        var configDir = _configDir.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(configDir))
        {
            _configDirStatus.IsVisible = false;
            return;
        }

        _configDirStatus.IsVisible = true;
        if (Directory.Exists(configDir))
        {
            ProviderConfigStatus.Set(_configDirStatus, "Folder found.", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_configDirStatus, "Folder does not exist — the profile cannot be saved until it does.", isOk: false);
        }
    }

    /// <summary>Resolves the claude executable against PATH (blank falls back to the bare "claude"), so the operator sees whether it is installed and where — informational, since a profile may pin a command for a machine that has it elsewhere.</summary>
    private void _UpdateExecutableStatus()
    {
        var command = _executablePath.Text?.Trim() is { Length: > 0 } path ? path : "claude";
        var resolved = ClaudeExecutableLocator.Resolve(command);
        if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_executableStatus, $"Found: {resolved}", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_executableStatus, "Not found on PATH — check claude is installed, or paste an absolute path.", isOk: false);
        }
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

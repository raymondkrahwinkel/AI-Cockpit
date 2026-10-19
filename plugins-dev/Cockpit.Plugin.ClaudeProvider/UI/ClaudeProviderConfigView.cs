using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.ClaudeProvider.UI;

// The "add/edit profile" config panel for the Claude provider (Fase 4, SDK route): the two fields the host's in-tree
// `ClaudeConfig` carried — an optional config directory (which login/config this profile reads) and an optional
// executable path. Both blank means a default session against the machine's own `claude` login, which is the
// common case, so nothing is required.
internal sealed class ClaudeProviderConfigView : IPluginProviderConfigView
{
    private const string _CheckFailedText = "Could not check this on the machine that runs the profile.";

    private readonly AutoCompleteBox _configDir;
    private readonly AutoCompleteBox _executablePath;
    private readonly CancellationTokenSource _lifetime = new();
    private int _checkTicket;
    private (string ConfigDir, bool Ok)? _checkedConfigDir;
    private readonly TextBlock _configDirStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _executableStatus = ProviderConfigStatus.CreateLine();

    private readonly ICockpitUiHost _host;
    private readonly ManagedCliConfigSection _managedCli;

    public Control View { get; }

    public ClaudeProviderConfigView(string? existingConfigJson, ICockpitUiHost host)
    {
        _host = host;
        var existing = ClaudeProviderConfig.Parse(existingConfigJson);
        // The panel refreshes the executable-status line after install/remove, so the two never disagree.
        _managedCli = new ManagedCliConfigSection(host, ClaudeManagedCli.CliName, "Claude CLI", _RequestCheck);

        _configDir = new AutoCompleteBox
        {
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            Text = existing.ConfigDir ?? string.Empty,
            PlaceholderText = "Blank = the machine's own ~/.claude login",
        };
        _executablePath = new AutoCompleteBox
        {
            MinimumPrefixLength = 0,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            Text = existing.ExecutablePath ?? string.Empty,
            PlaceholderText = "Blank = automatic: the managed copy, else \"claude\" on PATH",
        };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _LabelRow("Config directory (optional)", host.CreateHelpHint("setup", "config-dir")),
                _configDir,
                _configDirStatus,
                _LabelRow("Claude executable / path (optional)", host.CreateHelpHint("setup", "executable-path")),
                _executablePath,
                _executableStatus,
                _managedCli.View,
            },
        };

        // Live per-field feedback (#45): auto-detect the claude executable on PATH and flag a config directory that
        // does not exist — the two things that silently make a profile unusable.
        _configDir.TextChanged += (_, _) => _RequestCheck();
        _executablePath.TextChanged += (_, _) => _RequestCheck();
        _RequestCheck();
        _ = _LoadChoicesAsync();
    }

    // The profile may live on another machine than this view, so the backend part does the file-system checks.
    private void _RequestCheck()
    {
        var request = new ClaudeCheckConfigRequest(_configDir.Text?.Trim(), _executablePath.Text?.Trim());
        var ticket = ++_checkTicket;
        _checkedConfigDir = null;
        _ = _CheckAsync(request, ticket);
    }

    private async Task _CheckAsync(ClaudeCheckConfigRequest request, int ticket)
    {
        ClaudeCheckConfigAnswer? answer = null;
        try
        {
            var payload = JsonSerializer.SerializeToElement(request, ClaudeProviderChannel.Json);
            var raw = await _host.Channel.InvokeAsync(ClaudeProviderChannel.CheckConfig, payload, _lifetime.Token);
            answer = raw.Deserialize<ClaudeCheckConfigAnswer>(ClaudeProviderChannel.Json);
        }
        catch (Exception)
        {
            // Not blocking: an unreachable backend must not stop the operator from saving.
        }

        // Posted, so nothing awaits it: a failure shows on the status line instead of stopping the UI thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (ticket != _checkTicket)
            {
                return;
            }

            try
            {
                _ApplyAnswer(request, answer);
            }
            catch (Exception)
            {
                _executableStatus.IsVisible = true;
                ProviderConfigStatus.Set(_executableStatus, _CheckFailedText, isOk: true);
            }
        });
    }

    private void _ApplyAnswer(ClaudeCheckConfigRequest request, ClaudeCheckConfigAnswer? answer)
    {
        if (answer is null)
        {
            _checkedConfigDir = null;
            _configDirStatus.IsVisible = !string.IsNullOrEmpty(request.ConfigDir);
            ProviderConfigStatus.Set(_configDirStatus, _CheckFailedText, isOk: true);
            _executableStatus.IsVisible = true;
            ProviderConfigStatus.Set(_executableStatus, _CheckFailedText, isOk: true);
            return;
        }

        _checkedConfigDir = (request.ConfigDir ?? string.Empty, answer.ConfigDirOk);
        _configDirStatus.IsVisible = answer.ConfigDirText is not null;
        if (answer.ConfigDirText is not null)
        {
            ProviderConfigStatus.Set(_configDirStatus, answer.ConfigDirText, answer.ConfigDirOk);
        }

        _executableStatus.IsVisible = true;
        ProviderConfigStatus.Set(_executableStatus, answer.ExecutableText, answer.ExecutableOk);
    }

    private async Task _LoadChoicesAsync()
    {
        try
        {
            var raw = await _host.Channel.InvokeAsync(ClaudeProviderChannel.ConfigChoices, JsonSerializer.SerializeToElement(new { }), _lifetime.Token);
            var answer = raw.Deserialize<ClaudeConfigChoicesAnswer>(ClaudeProviderChannel.Json);
            if (answer is null)
            {
                return;
            }

            var dirs = answer.ConfigDirs.Select(choice => choice.ConfigDir).Where(dir => dir.Length > 0).Distinct().ToList();
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    _configDir.ItemsSource = dirs;
                    _executablePath.ItemsSource = answer.Executables;
                }
                catch (Exception)
                {
                    // Suggestions are a convenience; the fields still take free text.
                }
            });
        }
        catch (Exception)
        {
            // Suggestions are a convenience; free text still works without them.
        }
    }

    public bool TryGetConfigJson(out string configJson)
    {
        var configDir = _configDir.Text?.Trim();
        var executablePath = _executablePath.Text?.Trim();

        // A given config directory must exist — a typo here silently sends the CLI to an empty config and a
        // logged-out session. Only a finished check for the current text can say no; a pending or failed one never blocks.
        if (!string.IsNullOrEmpty(configDir) && _checkedConfigDir is { Ok: false } checkedDir && checkedDir.ConfigDir == configDir)
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

    // AC-1043: a label with the SDK-drawn "?" beside it, pointing at the section of this plugin's own setup
    // page that explains the field below.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 4, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };
}

using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: the "add/edit profile" config panel, mirroring KimiProviderConfigView's shape and login-button
// pattern. Caveat carried over from Kimi's own file: `_StartLogin` was never exercised against a rendered
// window or a real terminal in this environment.
internal sealed class OpencodeProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _command;
    private readonly TextBox _apiKey;
    private readonly TextBox _defaultModel;
    private readonly TextBlock _commandStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _loginStatus = ProviderConfigStatus.CreateLine();

    private readonly ICockpitHost _host;

    public Control View { get; }

    public OpencodeProviderConfigView(string? existingConfigJson, ICockpitHost host)
    {
        _host = host;
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<OpencodeConfig>(existingConfigJson, OpencodeConfig.JsonOptions);

        _command = new TextBox { Text = existing?.Command ?? "opencode" };
        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };
        _defaultModel = new TextBox
        {
            Text = existing?.DefaultModel ?? string.Empty,
            PlaceholderText = "e.g. anthropic/claude-sonnet-4-5 (blank = opencode's own default)",
        };

        // An API key is the primary auth route (passed as OPENCODE_API_KEY). This button is the second route
        // for a machine with none configured — opencode's own login flow needs a real terminal, which this
        // dialog's own stdio is not, so it opens one instead of trying to render the prompt itself.
        var loginButton = new Button { Content = "Login with opencode account…" };
        loginButton.Click += (_, _) => _StartLogin();

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _LabelRow("opencode command / path", host.CreateHelpHint("setup", "install-cli")),
                _command,
                _commandStatus,
                _LabelRow("API key (optional)", host.CreateHelpHint("setup", "authenticate")),
                _apiKey,
                _LabelRow("Default model (optional)", host.CreateHelpHint("setup", "default-model")),
                _defaultModel,
                _Hint("opencode routes by provider/model, e.g. anthropic/claude-sonnet-4-5, openai/gpt-5.1 — or leave blank to use opencode's own free-tier default and change it live from the session's model picker."),
                _Label("Sign in without an API key"),
                loginButton,
                new TextBlock
                {
                    Text = "Runs \"opencode auth login\" in its own terminal window — the login prompt needs a real terminal, which this dialog cannot provide.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
                _loginStatus,
            },
        };

        // Live per-field feedback (mirrors KimiProviderConfigView): auto-detect the executable on PATH so the
        // operator sees at once whether the command resolves and where.
        _command.TextChanged += (_, _) => _UpdateCommandStatus();
        _UpdateCommandStatus();
    }

    // Starts `opencode auth login` in a new console window — no stdio redirected and `CreateNoWindow` left at
    // its default `false`, so Windows allocates the child a brand-new visible console (same mechanism Kimi's
    // own login button relies on).
    private void _StartLogin()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_loginStatus, "Enter the opencode command above first.", isOk: false);
            return;
        }

        var executablePath = OpencodeExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                ArgumentList = { "auth", "login" },
            });
            ProviderConfigStatus.Set(_loginStatus, "Opened a terminal window for the login flow.", isOk: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            ProviderConfigStatus.Set(_loginStatus, $"Could not start the login flow: {exception.Message}", isOk: false);
        }
    }

    // Resolves the command exactly as a session spawn will (pin > managed > PATH) and states, in one line,
    // what will run — the same resolver `OpencodeAcpSessionDriverFactory` uses.
    private void _UpdateCommandStatus()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_commandStatus, "Required — enter \"opencode\" or an absolute path to the executable.", isOk: false);
            return;
        }

        var resolved = OpencodeExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
        if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Found: {resolved}", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_commandStatus, "Not found on PATH — install opencode (opencode.ai/docs), or paste an absolute path.", isOk: false);
        }
    }

    public bool TryGetConfigJson(out string configJson)
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            configJson = string.Empty;
            return false;
        }

        var config = new OpencodeConfig(
            Command: command,
            ApiKey: string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim(),
            DefaultModel: string.IsNullOrWhiteSpace(_defaultModel.Text) ? null : _defaultModel.Text.Trim());

        configJson = JsonSerializer.Serialize(config, OpencodeConfig.JsonOptions);
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };

    // AC-1043: a label with the SDK-drawn "?" beside it, pointing at the section of this plugin's own setup
    // page that explains the field below.
    private static StackPanel _LabelRow(string text, Control help) => new()
    {
        Orientation = Avalonia.Layout.Orientation.Horizontal,
        Margin = new Thickness(0, 4, 0, 0),
        Children = { new TextBlock { Text = text, FontSize = 11 }, help },
    };
}

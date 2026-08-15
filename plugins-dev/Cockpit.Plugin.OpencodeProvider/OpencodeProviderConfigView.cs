using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// The "add/edit profile" config panel for the opencode ACP provider (AC-783): the CLI command/path, an
// optional API key, and an optional default model — mirrors
// `Cockpit.Plugin.KimiProvider.KimiProviderConfigView`'s shape and its login-button pattern.
// P1-10c/IL#9-class caveat carried over from Kimi's own file: the login button's `_StartLogin` was never
// exercised against a rendered window or a real interactive terminal in this environment — built to the same
// shape and the same documented Win32 console-allocation behaviour Kimi's own view relies on, neither of
// which is empirically verified here either.
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

        // An API key above is the primary auth route — it is passed as OPENCODE_API_KEY (measured: the
        // documented env var opencode.ai's own ACP integration examples pass through). This button is the
        // second route, for a machine with no key configured: it starts opencode's own device-code-style
        // login flow (measured live: initialize's authMethods advertises exactly one, "opencode-login",
        // described as "Run `opencode auth login` in the terminal"). That flow needs an interactive terminal,
        // which this app's own stdio is not, so the button opens one rather than trying to render the prompt
        // itself — same reasoning as Kimi's own login button.
        var loginButton = new Button { Content = "Login with opencode account…" };
        loginButton.Click += (_, _) => _StartLogin();

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("opencode command / path"),
                _command,
                _commandStatus,
                _Label("API key (optional)"),
                _apiKey,
                _Label("Default model (optional)"),
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
}

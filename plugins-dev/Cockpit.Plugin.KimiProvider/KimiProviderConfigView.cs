using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

// The "add/edit profile" config panel for the Kimi ACP provider (AC-268): the CLI command/path, an optional
// API key, and an optional default model — mirroring `Cockpit.Plugin.CliAgentProvider.CliAgentProviderConfigView`'s
// shape, trimmed to what sub [a] owns (no sandbox/managed-CLI/model-listing controls — those land in later subs).
// P1-10c/IL#9: the login button's `_StartLogin` was never exercised against a rendered window or a
// real `kimi` install in this environment — it is built to the same shape as
// `CliAgentProviderConfigView`'s controls and follows documented Win32 console-allocation behaviour (a GUI
// process starting a console executable with no stdio redirected and no console of its own gets a brand-new
// visible console window from Windows), but that behaviour itself is not empirically verified here either. Both
// the visual result and the actual popped-up terminal are unverified — see the fix report.
internal sealed class KimiProviderConfigView : IPluginProviderConfigView
{
    private readonly TextBox _command;
    private readonly TextBox _apiKey;
    private readonly TextBox _defaultModel;
    private readonly TextBlock _commandStatus = ProviderConfigStatus.CreateLine();
    private readonly TextBlock _loginStatus = ProviderConfigStatus.CreateLine();

    private readonly ICockpitHost _host;

    public Control View { get; }

    public KimiProviderConfigView(string? existingConfigJson, ICockpitHost host)
    {
        _host = host;
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<KimiConfig>(existingConfigJson, KimiConfig.JsonOptions);

        _command = new TextBox { Text = existing?.Command ?? "kimi" };
        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };
        _defaultModel = new TextBox
        {
            Text = existing?.DefaultModel ?? string.Empty,
            PlaceholderText = "e.g. kimi-k2 (blank = kimi's own default)",
        };

        // D9/P1-10c: an API key above is the primary auth route — it skips kimi's own auth gate entirely
        // (protocol §1). This button is the second route, for a machine with no key configured: it starts
        // kimi's own device-code login flow. That flow needs an interactive terminal, which this app's own
        // stdio is not, so the button opens one rather than trying to render the prompt itself.
        var loginButton = new Button { Content = "Login with Kimi account…" };
        loginButton.Click += (_, _) => _StartLogin();

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("Kimi command / path"),
                _command,
                _commandStatus,
                _Label("API key (optional)"),
                _apiKey,
                _Label("Default model (optional)"),
                _defaultModel,
                _Label("Sign in without an API key"),
                loginButton,
                new TextBlock
                {
                    Text = "Runs \"kimi acp --login\" in its own terminal window — the device-code login prompt needs a real terminal, which this dialog cannot provide.",
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
                _loginStatus,
            },
        };

        // Live per-field feedback (mirrors CliAgentProviderConfigView): auto-detect the executable on PATH so
        // the operator sees at once whether the command resolves and where.
        _command.TextChanged += (_, _) => _UpdateCommandStatus();
        _UpdateCommandStatus();
    }

    // Starts `kimi acp --login` (protocol §1's `type:"terminal"` auth method) in a new console
    // window. No stdio is redirected and `ProcessStartInfo.CreateNoWindow` is left at its default
    // `false`: spawned this way from a GUI process with no console of its own, Windows
    // allocates the child a brand-new visible console — the same mechanism behind "a console flashes when a
    // GUI app launches a console tool", used deliberately here instead of accidentally suppressed.
    private void _StartLogin()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_loginStatus, "Enter the Kimi command above first.", isOk: false);
            return;
        }

        var executablePath = KimiExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                ArgumentList = { "acp", "--login" },
            });
            ProviderConfigStatus.Set(_loginStatus, "Opened a terminal window for the device-code login.", isOk: true);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            ProviderConfigStatus.Set(_loginStatus, $"Could not start the login flow: {exception.Message}", isOk: false);
        }
    }

    // Resolves the command exactly as a session spawn will (pin &gt; managed &gt; PATH) and states, in one
    // line, what will run — the same resolver `KimiAcpSessionDriverFactory` uses.
    private void _UpdateCommandStatus()
    {
        var command = _command.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(command))
        {
            ProviderConfigStatus.Set(_commandStatus, "Required — enter \"kimi\" or an absolute path to the executable.", isOk: false);
            return;
        }

        var resolved = KimiExecutableLocator.Resolve(command, _host.ResolveManagedCliPath);
        if (Path.IsPathRooted(resolved) && File.Exists(resolved))
        {
            ProviderConfigStatus.Set(_commandStatus, $"Found: {resolved}", isOk: true);
        }
        else
        {
            ProviderConfigStatus.Set(_commandStatus, "Not found on PATH — install the kimi CLI, or paste an absolute path.", isOk: false);
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

        var config = new KimiConfig(
            Command: command,
            ApiKey: string.IsNullOrWhiteSpace(_apiKey.Text) ? null : _apiKey.Text.Trim(),
            DefaultModel: string.IsNullOrWhiteSpace(_defaultModel.Text) ? null : _defaultModel.Text.Trim());

        configJson = JsonSerializer.Serialize(config, KimiConfig.JsonOptions);
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
}

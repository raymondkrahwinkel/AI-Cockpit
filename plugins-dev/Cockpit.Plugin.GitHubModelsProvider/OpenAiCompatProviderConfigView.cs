using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubModelsProvider;

/// <summary>
/// The "add/edit profile" config panel for this plugin's GitHub Models provider (#63): an API-key field (a
/// GitHub personal access token, with a "?" tooltip pointing at where to create one and which scope it
/// needs), a model id (namespaced, e.g. <c>openai/gpt-4.1</c>), and the base URL (pre-filled with GitHub
/// Models' own endpoint, editable for e.g. an org-scoped inference URL). Built in code, mirroring the
/// Gemini/OpenAI provider plugin's <c>OpenAiCompatProviderConfigView</c> (#45).
/// </summary>
internal sealed class OpenAiCompatProviderConfigView : IPluginProviderConfigView
{
    private const string DefaultModel = "openai/gpt-4.1";

    private readonly TextBox _apiKey;
    private readonly TextBox _model;
    private readonly TextBox _baseUrl;

    public Control View { get; }

    public OpenAiCompatProviderConfigView(string? existingConfigJson, string defaultBaseUrl)
    {
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<OpenAiCompatConfig>(existingConfigJson, OpenAiCompatConfig.JsonOptions);

        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };
        _model = new TextBox { Text = existing?.Model ?? DefaultModel, PlaceholderText = "e.g. openai/gpt-4.1" };
        _baseUrl = new TextBox { Text = existing?.BaseUrl ?? defaultBaseUrl };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("API key"),
                SettingsHelpRow.Build(_apiKey, "GitHub personal access token with the models:read scope — create at github.com/settings/tokens."),
                _Label("Model"),
                _model,
                _Hint("Models are namespaced by publisher, e.g. openai/gpt-4.1, meta/llama-3.3-70b-instruct — see the catalog at github.com/marketplace/models."),
                _Label("Base URL"),
                _baseUrl,
            },
        };
    }

    public bool TryGetConfigJson(out string configJson)
    {
        if (string.IsNullOrWhiteSpace(_apiKey.Text) || string.IsNullOrWhiteSpace(_model.Text) || string.IsNullOrWhiteSpace(_baseUrl.Text))
        {
            configJson = string.Empty;
            return false;
        }

        configJson = JsonSerializer.Serialize(new OpenAiCompatConfig(_apiKey.Text.Trim(), _model.Text.Trim(), _baseUrl.Text.Trim()));
        return true;
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}

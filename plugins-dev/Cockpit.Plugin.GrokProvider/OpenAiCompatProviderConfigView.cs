using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GrokProvider;

// The "add/edit profile" config panel for this plugin's Grok provider (AC-724): an API-key field (a "?"
// tooltip pointing at where to create one), a model id, and the base URL (pre-filled with xAI's own
// endpoint, editable for e.g. a regional endpoint like eu-west-1.api.x.ai). Built in code, mirroring the
// Gemini/OpenAI, GitHub Models and OpenRouter provider plugins' `OpenAiCompatProviderConfigView`. No
// default model is pre-filled (AC-724 criterion 4) — xAI has deprecated four model names in the last three
// months, so baking one in here would go stale the same way; the placeholder names the current model
// without setting it.
internal sealed class OpenAiCompatProviderConfigView : IPluginProviderConfigView
{
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
        _model = new TextBox { Text = existing?.Model ?? string.Empty, PlaceholderText = "e.g. grok-4.6" };
        _baseUrl = new TextBox { Text = existing?.BaseUrl ?? defaultBaseUrl };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("API key"),
                SettingsHelpRow.Build(_apiKey, "console.x.ai -> API Keys — create a key there."),
                _Label("Model"),
                _model,
                _Hint("Models come and go quickly on xAI's side — see docs.x.ai/docs/models for the current list; an old name here will start returning errors once xAI retires it."),
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

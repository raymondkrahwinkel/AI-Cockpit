using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpenRouterProvider;

// AC-806: the "add/edit profile" config panel for this plugin's OpenRouter provider, mirroring the sibling
// OpenAiCompat plugins' own config view. The model field is a plain TextBox — OpenRouter's `vendor/model`
// strings need no dedicated parsing, they pass straight through as `ChatOptions.ModelId`.
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
        _model = new TextBox { Text = existing?.Model ?? string.Empty, PlaceholderText = "e.g. anthropic/claude-sonnet-4.5" };
        _baseUrl = new TextBox { Text = existing?.BaseUrl ?? defaultBaseUrl };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("API key"),
                SettingsHelpRow.Build(_apiKey, "openrouter.ai/settings/keys — create a key there."),
                _Label("Model"),
                _model,
                _Hint("OpenRouter routes by vendor/model, e.g. anthropic/claude-sonnet-4.5, openai/gpt-5.1 — see the catalog at openrouter.ai/models."),
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

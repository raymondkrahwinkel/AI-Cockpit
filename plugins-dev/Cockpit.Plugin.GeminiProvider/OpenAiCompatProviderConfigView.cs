using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GeminiProvider;

/// <summary>
/// The "add/edit profile" config panel for this plugin's Gemini/OpenAI providers (#45): an API-key field
/// (with a "?" tooltip pointing at where to get one), a model id, and the base URL (pre-filled with the
/// provider's default, editable for a custom OpenAI-compatible gateway). Built in code, mirroring the other
/// example plugins' settings views (e.g. <c>Cockpit.Plugin.YouTrack.YouTrackSettingsControl</c>).
/// </summary>
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
        _model = new TextBox { Text = existing?.Model ?? string.Empty, PlaceholderText = "e.g. gemini-2.5-flash or gpt-5-mini" };
        _baseUrl = new TextBox { Text = existing?.BaseUrl ?? defaultBaseUrl };

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("API key"),
                SettingsHelpRow.Build(_apiKey, "Google AI Studio -> API key (for Gemini), or platform.openai.com -> API keys (for OpenAI)."),
                _Label("Model"),
                _model,
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
}

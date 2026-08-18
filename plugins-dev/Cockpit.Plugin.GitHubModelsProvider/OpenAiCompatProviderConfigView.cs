using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.GitHubModelsProvider;

// The "add/edit profile" config panel for this plugin's GitHub Models provider (#63): an API-key field (a
// GitHub personal access token, with a "?" tooltip pointing at where to create one and which scope it
// needs), a model id (namespaced, e.g. `openai/gpt-4.1`) with fetched suggestions (AC-931), and the base
// URL (pre-filled with GitHub Models' own endpoint, editable for e.g. an org-scoped inference URL). Built
// in code, mirroring the Gemini/OpenAI provider plugin's `OpenAiCompatProviderConfigView` (#45).
internal sealed class OpenAiCompatProviderConfigView : IPluginProviderConfigView
{
    private const string DefaultModel = "openai/gpt-4.1";

    private readonly TextBox _apiKey;
    private readonly AutoCompleteBox _model;
    private readonly TextBox _baseUrl;
    private readonly Button _fetchModels;
    private readonly TextBlock _modelStatus = ProviderConfigStatus.CreateLine();

    public Control View { get; }

    public OpenAiCompatProviderConfigView(string? existingConfigJson, string defaultBaseUrl)
    {
        var existing = string.IsNullOrWhiteSpace(existingConfigJson)
            ? null
            : JsonSerializer.Deserialize<OpenAiCompatConfig>(existingConfigJson, OpenAiCompatConfig.JsonOptions);

        _apiKey = new TextBox { Text = existing?.ApiKey ?? string.Empty, PasswordChar = '•' };

        // Free text with fetched suggestions, not a hard dropdown: the catalog may not list every model a
        // user has access to, and MinimumPrefixLength=0 opens the list on a click instead of only on typing.
        _model = new AutoCompleteBox
        {
            Text = existing?.Model ?? DefaultModel,
            PlaceholderText = "e.g. openai/gpt-4.1",
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 0,
            IsTextCompletionEnabled = false,
        };
        _baseUrl = new TextBox { Text = existing?.BaseUrl ?? defaultBaseUrl };

        _fetchModels = new Button { Content = "Fetch", Margin = new Thickness(6, 0, 0, 0) };
        _fetchModels.Click += (_, _) => _ = _FetchModelsAsync();
        _modelStatus.IsVisible = false;

        View = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _Label("API key"),
                SettingsHelpRow.Build(_apiKey, "GitHub personal access token with the models:read scope — create at github.com/settings/tokens."),
                _Label("Model"),
                _ModelRow(),
                _modelStatus,
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

    private Control _ModelRow()
    {
        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_fetchModels, Dock.Right);
        row.Children.Add(_fetchModels);
        row.Children.Add(_model);
        return row;
    }

    // AC-931: fills the suggestions from the base URL's own `/models`, best-effort — a rejected token or an
    // unreachable endpoint leaves the field as free text with a status line saying so.
    private async Task _FetchModelsAsync()
    {
        var apiKey = _apiKey.Text?.Trim() ?? string.Empty;
        var baseUrl = _baseUrl.Text?.Trim() ?? string.Empty;
        _modelStatus.IsVisible = true;

        if (apiKey.Length == 0 || baseUrl.Length == 0)
        {
            ProviderConfigStatus.Set(_modelStatus, "Fill in the API key and base URL first.", isOk: false);
            return;
        }

        _fetchModels.IsEnabled = false;
        ProviderConfigStatus.Set(_modelStatus, "Loading…", isOk: true);
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var models = await OpenAiCompatModelCatalog.ListAsync(httpClient, baseUrl, apiKey, CancellationToken.None).ConfigureAwait(true);
            _model.ItemsSource = models;
            ProviderConfigStatus.Set(
                _modelStatus,
                models.Count == 0
                    ? "This base URL listed no models — type the id by hand."
                    : $"Found {models.Count} model(s) — click the field to pick one, or type an id.",
                isOk: models.Count > 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or NotSupportedException or UriFormatException or InvalidOperationException)
        {
            ProviderConfigStatus.Set(_modelStatus, "Could not list models here — this endpoint may not serve /models, or the token was rejected. Type the id by hand.", isOk: false);
        }
        finally
        {
            _fetchModels.IsEnabled = true;
        }
    }

    private static TextBlock _Label(string text) => new() { Text = text, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}

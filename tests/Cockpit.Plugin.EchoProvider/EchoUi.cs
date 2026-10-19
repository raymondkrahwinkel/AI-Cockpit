using System.Text.Json.Nodes;
using Avalonia.Controls;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.EchoProvider;

// AC-1571: a settings view shaped like the plugins' own (Discord's): one plain field and one credential read back into
// a masked box, written on Save. Nothing in it knows whether its storage is this machine's or a server's.
public sealed class EchoUi : ICockpitPluginUi
{
    public const string ReadSettings = "read-settings";

    public const string GreetingKey = "greeting";

    public const string ApiTokenKey = "apiToken";

    public void InitializeUi(ICockpitUiHost host)
    {
        host.AddSettings(() => new EchoSettingsView(host.Storage));
        host.AddProviderConfigView(EchoProviderPlugin.ProviderId, configJson => new EchoConfigView(configJson));
    }
}

// AC-1534/AC-1585: the provider's own profile panel, a base URL and a key, as an OpenAI-compatible provider has them.
public sealed class EchoConfigView : IPluginProviderConfigView
{
    public EchoConfigView(string? configJson)
    {
        var config = configJson is null ? null : JsonNode.Parse(configJson);
        BaseUrl.Text = config?["baseUrl"]?.GetValue<string>();
        ApiKey.Text = config?["apiKey"]?.GetValue<string>();
        View = new StackPanel { Children = { BaseUrl, ApiKey } };
    }

    public TextBox BaseUrl { get; } = new() { Name = "BaseUrl" };

    public TextBox ApiKey { get; } = new() { Name = "ApiKey", PasswordChar = '*' };

    public Control View { get; }

    public bool TryGetConfigJson(out string configJson)
    {
        configJson = new JsonObject { ["baseUrl"] = BaseUrl.Text ?? "", ["apiKey"] = ApiKey.Text ?? "" }.ToJsonString();
        return true;
    }
}

public sealed class EchoSettingsView : StackPanel, IPluginSettingsView
{
    private readonly IPluginStorage _storage;

    public EchoSettingsView(IPluginStorage storage)
    {
        _storage = storage;
        Spacing = 8;
        Greeting = new TextBox { PlaceholderText = "Greeting", Text = storage.Get<string>(EchoUi.GreetingKey) ?? "" };
        ApiToken = new TextBox { PlaceholderText = "API token", PasswordChar = '•', Text = storage.GetSecret(EchoUi.ApiTokenKey) ?? "" };
        Children.Add(new TextBlock { Text = "Greeting" });
        Children.Add(Greeting);
        Children.Add(new TextBlock { Text = "API token" });
        Children.Add(ApiToken);
    }

    public TextBox Greeting { get; }

    public TextBox ApiToken { get; }

    public bool TryStage(out Action? commit, out string? error)
    {
        var greeting = Greeting.Text ?? "";
        var apiToken = ApiToken.Text ?? "";
        commit = () =>
        {
            _storage.Set(EchoUi.GreetingKey, greeting);
            _storage.SetSecret(EchoUi.ApiTokenKey, apiToken);
        };
        error = null;
        return true;
    }
}

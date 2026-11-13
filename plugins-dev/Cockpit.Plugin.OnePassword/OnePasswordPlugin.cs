using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.OnePassword;

// AC-2348: 1Password as a provider of the host's vault hub. Its connect page lives in the UI part and reaches this
// part through the plugin channel only.
public sealed class OnePasswordPlugin : ICockpitPlugin
{
    private readonly List<IDisposable> _registrations = [];
    private OnePasswordProvider? _provider;
    private EventHandler? _availability;

    public PluginMetadata Metadata { get; } = new(
        Id: "onepassword",
        DisplayName: "1Password",
        Author: "Cockpit",
        Description: "Connects one 1Password vault to Cockpit's vault through a service account and the official `op` CLI, so agents can use its items without seeing a value. The service account must read exactly one vault; the token is stored encrypted by the vault, which needs Cockpit's credential encryption to be on.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        var provider = new OnePasswordProvider(new OnePasswordCli(host.ResolveManagedCliPath));
        provider.Bind(host.AddVaultProvider(provider));
        _provider = provider;

        _registrations.Add(host.Channel.Handle(OnePasswordChannel.Status, async (_, ct) => _Reply(await provider.StatusAsync(ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(OnePasswordChannel.Test, async (payload, ct) => _Reply(await provider.TestAsync(_Token(payload), ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(OnePasswordChannel.Connect, async (payload, ct) => _Reply(await provider.ConnectAsync(_Token(payload), ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(OnePasswordChannel.Disconnect, (_, _) =>
        {
            provider.Disconnect();
            return Task.FromResult(_Reply(true));
        }));
        _registrations.Add(host.Channel.Handle(OnePasswordChannel.OpenSecurity, (_, _) =>
        {
            provider.OpenEncryptionSettings();
            return Task.FromResult(_Reply(true));
        }));

        _availability = (_, _) => host.Channel.Publish(OnePasswordChannel.AvailabilityChanged, _Reply(provider.Availability.ToString()));
        provider.AvailabilityChanged += _availability;
    }

    public void Dispose()
    {
        if (_provider is { } provider && _availability is { } handler)
        {
            provider.AvailabilityChanged -= handler;
        }

        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }

    private static string _Token(JsonElement payload) =>
        payload.Deserialize<TokenRequest>(OnePasswordChannel.Json)?.Token.Trim() ?? string.Empty;

    private static JsonElement _Reply<T>(T value) => JsonSerializer.SerializeToElement(value, OnePasswordChannel.Json);
}

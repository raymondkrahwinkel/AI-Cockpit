using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.Bitwarden;

// AC-2349: Bitwarden Secrets Manager as a provider of the host's vault hub. Its connect page lives in the UI part and reaches this
// part through the plugin channel only.
public sealed class BitwardenPlugin : ICockpitPlugin
{
    private readonly List<IDisposable> _registrations = [];
    private BitwardenProvider? _provider;
    private EventHandler? _availability;

    public PluginMetadata Metadata { get; } = new(
        Id: "bitwarden",
        DisplayName: "Bitwarden",
        Author: "Cockpit",
        Description: "Connects one Bitwarden Secrets Manager project to Cockpit's vault through a machine account and the official `bws` CLI, so agents can use its secrets without seeing a value. The machine account must read exactly one project; the token is stored encrypted by the vault, which needs Cockpit's credential encryption to be on.");

    public void ConfigureServices(IServiceCollection services)
    {
    }

    public void Initialize(ICockpitHost host)
    {
        // Bitwarden publishes SHA-256 checksums for bws, so the host may install it; a pinned or PATH copy still wins.
        host.AddManagedCli(BitwardenManagedCli.Descriptor);
        var provider = new BitwardenProvider(new BitwardenCli(host.ResolveManagedCliPath), () => host.Storage.Get<bool>("allowAgentWrites"));
        provider.Bind(host.AddVaultProvider(provider));
        _provider = provider;

        _registrations.Add(host.Channel.Handle(BitwardenChannel.Status, async (_, ct) => _Reply(await provider.StatusAsync(ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(BitwardenChannel.Test, async (payload, ct) => _Reply(await provider.TestAsync(_Token(payload), ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(BitwardenChannel.Connect, async (payload, ct) => _Reply(await provider.ConnectAsync(_Token(payload), ct).ConfigureAwait(false))));
        _registrations.Add(host.Channel.Handle(BitwardenChannel.Disconnect, (_, _) =>
        {
            provider.Disconnect();
            return Task.FromResult(_Reply(true));
        }));
        _registrations.Add(host.Channel.Handle(BitwardenChannel.OpenSecurity, (_, _) =>
        {
            provider.OpenEncryptionSettings();
            return Task.FromResult(_Reply(true));
        }));

        _availability = (_, _) => host.Channel.Publish(BitwardenChannel.AvailabilityChanged, _Reply(provider.Availability.ToString()));
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
        payload.Deserialize<TokenRequest>(BitwardenChannel.Json)?.Token.Trim() ?? string.Empty;

    private static JsonElement _Reply<T>(T value) => JsonSerializer.SerializeToElement(value, BitwardenChannel.Json);
}

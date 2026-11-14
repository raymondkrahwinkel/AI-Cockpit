using System.Text.Json;
using System.Text.Json.Serialization;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Plugins.Abstractions.Vault;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Vault;

// AC-2336: the host's vault hub. Stores and reads nothing unless credential encryption is on and unlocked; only ClearCredential deletes (AC-2364).
internal sealed partial class VaultHub : ISingletonService
{
    internal const int AuditCapacity = 500;

    private static readonly JsonSerializerOptions StateOptions = new();

    private readonly ISecretProtectionService _protection;
    private readonly CockpitConfigFileAccess _config;
    private readonly ISecretKeyHolder _keyHolder;
    private readonly ILogger<VaultHub> _logger;
    private readonly IToastService? _toasts;
    private readonly Dictionary<string, IVaultProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private VaultAvailability? _last;
    private Func<Task>? _openSecurity;

    public VaultHub(ISecretProtectionService protection, ILogger<VaultHub> logger, IToastService? toasts = null)
        : this(protection, CockpitConfigPath.Default, SecretKeyHolder.Shared, logger, toasts)
    {
    }

    // Test seam: its own config file and key holder, the same pair the SecretProtectionService under test uses.
    internal VaultHub(
        ISecretProtectionService protection,
        string configFilePath,
        ISecretKeyHolder keyHolder,
        ILogger<VaultHub> logger,
        IToastService? toasts = null)
    {
        _protection = protection;
        _config = new CockpitConfigFileAccess(configFilePath, keyHolder);
        _keyHolder = keyHolder;
        _logger = logger;
        _toasts = toasts;
    }

    public event EventHandler? AvailabilityChanged;

    public IReadOnlyList<IVaultProvider> Providers
    {
        get
        {
            lock (_gate)
            {
                return [.. _providers.Values];
            }
        }
    }

    // Null when another provider already holds this id. `pluginId` is the id the host established for the registrant,
    // never one the plugin chose: it owns the credential slot, so a plugin that claims another's provider id later
    // gets an empty slot of its own instead of that provider's token (AC-2333 S1).
    public IVaultProviderContext? Register(string pluginId, IVaultProvider provider)
    {
        lock (_gate)
        {
            if (!_providers.TryAdd(provider.Id, provider))
            {
                return null;
            }
        }

        return new ProviderContext(this, provider, _SlotOf(pluginId, provider.Id));
    }

    public void Unregister(IVaultProvider provider)
    {
        lock (_gate)
        {
            if (_providers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, provider))
            {
                _providers.Remove(provider.Id);
            }
        }
    }

    // How the desktop opens Options › Security; a host without a window leaves it unset.
    public void UseEncryptionSettings(Func<Task> open) => _openSecurity = open;

    public void OpenEncryptionSettings()
    {
        if (_openSecurity is { } open)
        {
            _ = _OpenAsync(open);
        }
    }

    // The start-up check: logs a stored state that no longer decrypts; an unavailable vault is refused, never cleared (AC-2364).
    public async Task CheckAtStartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (await EvaluateAsync(cancellationToken).ConfigureAwait(false) == VaultAvailability.Available)
            {
                await _ReadStateAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The vault's start-up check failed; the vault stays closed until its next use.");
        }
    }

    public async Task<VaultAvailability> EvaluateAsync(CancellationToken cancellationToken = default)
    {
        var status = await _protection.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        var availability = !status.Enabled
            ? VaultAvailability.EncryptionOff
            : status.Unlocked ? VaultAvailability.Available : VaultAvailability.Locked;

        _Note(availability);
        return availability;
    }

    // The audit ring: the newest AuditCapacity events, names only. False while the vault is not available.
    public async Task<bool> TryAppendAuditAsync(VaultAuditEvent entry, CancellationToken cancellationToken = default)
    {
        var appended = await _TryUpdateAsync(section =>
        {
            var ring = _Parse<List<VaultAuditEvent>>(section.AuditSecret) ?? [];
            ring.Add(entry);
            section.AuditSecret = JsonSerializer.Serialize(ring.TakeLast(AuditCapacity), StateOptions);
        }, cancellationToken).ConfigureAwait(false);
        if (appended)
        {
            _RaiseChanged();
        }

        return appended;
    }

    private VaultAvailability _Evaluate() => EvaluateAsync().GetAwaiter().GetResult();

    private static string _SlotOf(string pluginId, string providerId) => JsonSerializer.Serialize<string[]>([pluginId, providerId], StateOptions);

    private bool _TryStore(string slot, string value) =>
        _TryUpdateAsync(section => _WithState(section, state => state.Credentials[slot] = value), CancellationToken.None)
            .GetAwaiter().GetResult();

    // Disconnect is the one explicit delete (AC-2364): the credential and every grant on its provider's items go together.
    private void _Clear(string slot, string scheme)
    {
        bool Owned(string key) => ReferenceOf(key).StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (var keys in _sessionGrants.Values)
            {
                keys.RemoveWhere(Owned);
            }

            _generation++;
        }

        _TryUpdateAsync(section => _WithState(section, state =>
        {
            state.Credentials.Remove(slot);
            foreach (var keys in state.ProjectGrants.Values)
            {
                keys.RemoveAll(Owned);
            }
        }), CancellationToken.None).GetAwaiter().GetResult();
        _RaiseChanged();
    }

    private string? _Read(string slot)
    {
        if (_Evaluate() != VaultAvailability.Available)
        {
            return null;
        }

        var state = _ReadStateAsync(CancellationToken.None).GetAwaiter().GetResult();
        return state?.Credentials.GetValueOrDefault(slot);
    }

    // Null means: nothing readable, so nothing is granted.
    private async Task<VaultState?> _ReadStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var file = await _config.ReadAsync(cancellationToken).ConfigureAwait(false);
            return _Parse<VaultState>(file?.Vault?.StateSecret) ?? new VaultState();
        }
        catch (Exception exception) when (exception is SecretProtectionException or JsonException)
        {
            _logger.LogWarning(exception, "The vault's stored state could not be read; it is treated as empty.");
            return null;
        }
    }

    // The write goes through the shared write route; the encryption check is repeated under its gate, so nothing lands in the clear.
    private async Task<bool> _TryUpdateAsync(Action<VaultSectionEntry> mutate, CancellationToken cancellationToken)
    {
        if (await EvaluateAsync(cancellationToken).ConfigureAwait(false) != VaultAvailability.Available)
        {
            return false;
        }

        try
        {
            await _config.UpdateAsync(file =>
            {
                if (file.Security is not { Enabled: true } || _keyHolder.Protector is null)
                {
                    throw new VaultRefusedException();
                }

                var section = file.Vault ?? new VaultSectionEntry();
                mutate(section);
                file.Vault = section;
            }, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (VaultRefusedException)
        {
            return false;
        }
        catch (Exception exception) when (exception is SecretProtectionException or JsonException)
        {
            _logger.LogWarning(exception, "The vault's stored state could not be read; nothing was written.");
            return false;
        }
    }

    private static void _WithState(VaultSectionEntry section, Action<VaultState> change)
    {
        var state = _Parse<VaultState>(section.StateSecret) ?? new VaultState();
        change(state);
        section.StateSecret = JsonSerializer.Serialize(state, StateOptions);
    }

    private static T? _Parse<T>(string? json) where T : class =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, StateOptions);

    private void _Note(VaultAvailability availability)
    {
        lock (_gate)
        {
            var changed = _last is { } last && last != availability;
            _last = availability;
            _isUnavailableToastShown &= availability != VaultAvailability.Available;
            if (!changed)
            {
                return;
            }
        }

        AvailabilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool _Holds(IVaultProvider provider)
    {
        lock (_gate)
        {
            return _providers.TryGetValue(provider.Id, out var current) && ReferenceEquals(current, provider);
        }
    }

    private async Task _OpenAsync(Func<Task> open)
    {
        try
        {
            await open().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Opening Options › Security for the vault failed.");
        }
    }

    private sealed class VaultRefusedException : Exception;

    private sealed class VaultState
    {
        [JsonPropertyName("credentials")]
        public Dictionary<string, string> Credentials { get; set; } = new(StringComparer.Ordinal);

        [JsonPropertyName("projectGrants")]
        public Dictionary<string, List<string>> ProjectGrants { get; set; } = new(StringComparer.Ordinal);
    }

    // A provider's slot, keyed by (owning plugin, provider id); a context whose provider was unregistered or replaced answers like a closed vault.
    private sealed class ProviderContext(VaultHub hub, IVaultProvider provider, string slot) : IVaultProviderContext
    {
        public VaultAvailability Availability => hub._Evaluate();

        public event EventHandler? AvailabilityChanged
        {
            add => hub.AvailabilityChanged += value;
            remove => hub.AvailabilityChanged -= value;
        }

        public bool TryStoreCredential(string value) => hub._Holds(provider) && hub._TryStore(slot, value);

        public string? ReadCredential() => hub._Holds(provider) ? hub._Read(slot) : null;

        public void ClearCredential()
        {
            if (hub._Holds(provider))
            {
                hub._Clear(slot, provider.ReferenceScheme);
            }
        }

        public void OpenEncryptionSettings() => hub.OpenEncryptionSettings();
    }
}

// One audit line: who, which item, when — never a value.
internal sealed record VaultAuditEvent(DateTimeOffset At, string Kind, string? Session, string? Item, int? Count = null);

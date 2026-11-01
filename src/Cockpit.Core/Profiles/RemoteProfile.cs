using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cockpit.Core.Profiles;

// AC-1473: a server's profile over the admin connection, no credential in it. AC-1475: `KnownValues` per declared option.
// AC-1534: `ConfigJson` is a plugin's config, secrets masked; `HasApiKey` says one is set. AC-1585: `Profile` is the
// whole profile as the editor holds it, each secret masked; `Full` is it read back.
public sealed record RemoteProfile(
    string Label,
    string Provider,
    string? Model,
    string? PermissionMode,
    IReadOnlyList<string>? McpServers,
    IReadOnlyDictionary<string, string>? OptionDefaults,
    DelegationPolicy Delegation,
    IReadOnlyList<RemoteProfileVariable> Environment,
    bool HasApiKey,
    bool ConfiguredOnServer,
    ProfileSignInKind? SignIn,
    IReadOnlyDictionary<string, IReadOnlyList<RemoteOptionValue>>? KnownValues = null,
    string? ConfigJson = null,
    JsonElement? Profile = null)
{
    [JsonIgnore]
    public SessionProfile? Full { get; init; }
}

// AC-1475: one value a provider offers for an option, as its descriptor names it.
public sealed record RemoteOptionValue(string Value, string Label);

// `Value` is null for a secret: it stays on the server.
public sealed record RemoteProfileVariable(string Key, string? Value, bool IsSecret = false);

// AC-1473: what one change names; a field left null stays as the server has it. An empty model clears it, and
// `Environment` replaces the plain variables only, never a secret one. AC-1534: `ConfigJson` replaces a plugin
// profile's config, except that a secret field left blank keeps the value the server holds.
public sealed record RemoteProfilePatch
{
    public string? Model { get; init; }

    public string? PermissionMode { get; init; }

    public RemoteMcpSelection? McpServers { get; init; }

    public IReadOnlyList<RemoteProfileVariable>? Environment { get; init; }

    public DelegationPolicy? Delegation { get; init; }

    public string? ConfigJson { get; init; }

    // AC-1585: the whole profile; the server keeps each secret that reads as the mask. Its label renames.
    public JsonElement? Profile { get; init; }
}

// `Names` null is every enabled server, as `SessionProfile.EnabledMcpServerNames` has it.
public sealed record RemoteMcpSelection(IReadOnlyList<string>? Names);

// AC-1473: a new profile on a server: one of its plugin providers, configured there, plus the settings a change names.
public sealed record RemoteNewProfile(string Label, string Provider, RemoteProfilePatch? Settings = null);

// AC-1585: a provider a profile on the server can name, as the profile editor needs it. `Owner` is the folder of the
// plugin that registered it there; none for a provider built into the cockpit.
public sealed record RemoteProviderInfo(
    string Id,
    string Label,
    string? Owner,
    bool SupportsEnvVars,
    bool SupportsPermissions,
    bool HasTty,
    IReadOnlyList<RemoteProviderOption> Options,
    RemoteTermsNotice? TermsNotice = null);

public sealed record RemoteTermsNotice(string Text, Uri Url, string? SubscriptionText = null, string? ApiKeyText = null, Uri? ApiTermsUrl = null);

public sealed record RemoteProviderOption(string Key, string Label, IReadOnlyList<string> Choices, IReadOnlyDictionary<string, string>? ChoiceLabels = null);

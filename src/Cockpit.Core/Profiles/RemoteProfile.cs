namespace Cockpit.Core.Profiles;

// AC-1473 (F5.6b3): a server's profile as it crosses the admin connection, with no credential in it. AC-1475:
// `KnownValues` lists what the provider offers per declared option; a free-form option, or an older server, has none.
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
    IReadOnlyDictionary<string, IReadOnlyList<RemoteOptionValue>>? KnownValues = null);

// AC-1475: one value a provider offers for an option, as its descriptor names it.
public sealed record RemoteOptionValue(string Value, string Label);

// `Value` is null for a secret: it stays on the server.
public sealed record RemoteProfileVariable(string Key, string? Value, bool IsSecret = false);

// AC-1473: what one change names; a field left null stays as the server has it. An empty model clears it, and
// `Environment` replaces the plain variables only, never a secret one.
public sealed record RemoteProfilePatch
{
    public string? Model { get; init; }

    public string? PermissionMode { get; init; }

    public RemoteMcpSelection? McpServers { get; init; }

    public IReadOnlyList<RemoteProfileVariable>? Environment { get; init; }

    public DelegationPolicy? Delegation { get; init; }
}

// `Names` null is every enabled server, as `SessionProfile.EnabledMcpServerNames` has it.
public sealed record RemoteMcpSelection(IReadOnlyList<string>? Names);

// AC-1473: a new profile on a server: one of its plugin providers, configured there, plus the settings a change names.
public sealed record RemoteNewProfile(string Label, string Provider, RemoteProfilePatch? Settings = null);

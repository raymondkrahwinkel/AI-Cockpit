using System.Text.Json;

namespace Cockpit.Core.Provisioning;

// AC-1505: the provisioning manifest "cockpit.provision" v1, as designed in DB-0.1 (Depot cockpit,
// Designs/depot-als-basis/manifest-v1.md). Names follow that document; JsonElement where it leaves a shape open.
public sealed record ProvisionManifest
{
    public required string Format { get; init; }

    public required int FormatVersion { get; init; }

    public List<string> Requires { get; init; } = [];

    public string? ManifestId { get; init; }

    public long? Revision { get; init; }

    public DateTimeOffset? IssuedAt { get; init; }

    public ProvisionIssuer? Issuer { get; init; }

    public ProvisionPlugins? Plugins { get; init; }

    public ProvisionStores? Stores { get; init; }

    public Dictionary<string, ProvisionSetting>? Settings { get; init; }

    public ProvisionProfiles? Profiles { get; init; }

    public ProvisionProjects? Projects { get; init; }

    public ProvisionAssistant? Assistant { get; init; }

    public ProvisionPolicy? Policy { get; init; }

    public ProvisionBackup? Backup { get; init; }

    public List<ProvisionConnection>? Connections { get; init; }

    public ProvisionFirstRun? FirstRun { get; init; }

    public List<ProvisionNote> Notes { get; init; } = [];
}

public sealed record ProvisionIssuer(string Kind, ProvisionNamedRef? Organization = null, ProvisionNamedRef? InstallationSet = null, ProvisionNamedRef? Profile = null);

public sealed record ProvisionNamedRef(string Id, string? Name = null);

// `Allowed` is "*" or a list of ids (§3.1).
public sealed record ProvisionPlugins(
    List<ProvisionRequiredPlugin>? Required = null,
    JsonElement? Allowed = null,
    List<string>? Forbidden = null,
    List<ProvisionSelectedPlugin>? Selected = null);

public sealed record ProvisionRequiredPlugin(string Id, string? MinVersion = null, string? Pin = null, string? Store = null);

public sealed record ProvisionSelectedPlugin(string Id, string? MinVersion = null);

public sealed record ProvisionStores(List<ProvisionStore>? Allowed = null, bool? AllowUserStores = null, List<ProvisionStore>? Added = null);

public sealed record ProvisionStore(string Id, string Url, string Auth);

public sealed record ProvisionSetting(JsonElement Value, string Mode, string By);

public sealed record ProvisionProfiles(List<ProvisionProfile>? Items = null, ProvisionAssistantProfile? AssistantProfile = null, string? DefaultProfile = null);

public sealed record ProvisionProfile
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    public string Mode { get; init; } = ProvisionManifestReader.DefaultMode;

    public required string By { get; init; }

    public ProvisionProvider? Provider { get; init; }

    public string? Purpose { get; init; }

    public ProvisionProfileDefaults? Defaults { get; init; }

    public ProvisionDelegation? Delegation { get; init; }

    public List<ProvisionEnvironmentVariable>? Environment { get; init; }

    public List<string>? EnabledMcpServers { get; init; }

    public string? SystemPrompt { get; init; }

    public string? DefaultKind { get; init; }

    public int? MemoryCapMegabytes { get; init; }

    public JsonElement? Assistant { get; init; }
}

public sealed record ProvisionProvider(string Id, string? Model = null);

public sealed record ProvisionProfileDefaults(
    string? PermissionMode = null,
    string? Model = null,
    string? Effort = null,
    Dictionary<string, string>? OptionDefaults = null,
    string? ReadingLevel = null,
    bool? AutoApproveTools = null);

public sealed record ProvisionDelegation(
    bool? AllowedAsTarget = null,
    int? MaxConcurrent = null,
    string? PermissionCeiling = null,
    bool? MayDelegateFurther = null,
    int? TimeoutMinutes = null,
    List<string>? AllowedTools = null);

// A secret variable travels as its name only; the value is asked for on the machine (§3.4).
public sealed record ProvisionEnvironmentVariable(string Name, string? Value = null, bool? Secret = null);

public sealed record ProvisionAssistantProfile(string Label, string Mode, string By, bool? ReplacesStandingInstruction = null);

public sealed record ProvisionProjects(List<ProvisionSharedProject>? Shared = null, List<ProvisionOwnProject>? Own = null);

public sealed record ProvisionSharedProject(string Connection, string? Org, string Slug, string By);

public sealed record ProvisionOwnProject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public string? Category { get; init; }

    public string? GitUrl { get; init; }

    public string? DefaultProfileLabel { get; init; }

    public string? BehaviorPrompt { get; init; }

    public JsonElement? Assistant { get; init; }

    public bool? IsolateInWorktreeByDefault { get; init; }

    public JsonElement? AdditionalInfo { get; init; }

    public JsonElement? Jobs { get; init; }

    public string? MemoryRef { get; init; }

    public JsonElement? Resources { get; init; }

    public JsonElement? PluginFields { get; init; }
}

public sealed record ProvisionAssistant(ProvisionOrgRules? OrgRules = null);

public sealed record ProvisionOrgRules(string By, string Content, string? Title = null, string? Revision = null);

public sealed record ProvisionPolicy(
    string By,
    ProvisionProviderPolicy? Providers = null,
    ProvisionPermissionPolicy? Permissions = null,
    ProvisionBackupPolicy? Backup = null,
    ProvisionAccountPolicy? Account = null);

public sealed record ProvisionProviderPolicy(List<ProvisionAllowedProvider>? Allowed = null);

// `Models` is "*" or a list of model ids.
public sealed record ProvisionAllowedProvider(string Id, JsonElement Models);

public sealed record ProvisionPermissionPolicy(
    string? MaxPermissionMode = null,
    string? AutoApproveTools = null,
    string? DelegationCeiling = null,
    string? TerminalAccess = null,
    string? ShellAccess = null,
    bool? NeverSend = null);

public sealed record ProvisionBackupPolicy(
    bool? Required = null,
    int? MaxIntervalHours = null,
    int? MinKeep = null,
    List<string>? AllowedTargets = null,
    string? IncludeCredentials = null);

public sealed record ProvisionAccountPolicy(bool? Require2fa = null);

public sealed record ProvisionBackup(ProvisionBackupTarget? Target = null, ProvisionBackupSchedule? Schedule = null, string? RestoreFrom = null);

public sealed record ProvisionBackupTarget(string Connection, string By);

public sealed record ProvisionBackupSchedule(string By, int? IntervalHours = null, int? Keep = null);

// The core reads `Id`, `Plugin` and `Required` only; `Config` goes unread to the plugin that claims it (§3.9).
public sealed record ProvisionConnection(string Id, string Plugin, bool Required, JsonElement? Config = null);

public sealed record ProvisionFirstRun(string? WorkKind = null, string? ReadingLevel = null, bool? SimpleView = null);

// A source note (§4.1), or one the reader adds for what it stripped: reason "skipped-unknown" / "skipped-not-portable".
public sealed record ProvisionNote(string Path, string Reason, string? By = null, string? Detail = null);

using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

// AC-22: on-disk shape of one profile environment variable. A secret goes under `SecretValue`, a name
// `SecretFields` recognises, so it is encrypted without this entry knowing how; a plain value stays
// readable under `Value` — the config file is the operator's to inspect.
internal sealed class ProfileEnvironmentVariableEntry
{
    public string Key { get; set; } = string.Empty;

    // The value when it is not a credential.
    public string? Value { get; set; }

    // The value when it is a credential; the field's name is what routes it through encryption.
    public string? SecretValue { get; set; }

    public static ProfileEnvironmentVariableEntry FromDomain(ProfileEnvironmentVariable variable) => new()
    {
        Key = variable.Key,
        Value = variable.IsSecret ? null : variable.Value,
        SecretValue = variable.IsSecret ? variable.Value : null,
    };

    public ProfileEnvironmentVariable ToDomain() =>
        new(Key, SecretValue ?? Value ?? string.Empty, IsSecret: SecretValue is not null);
}

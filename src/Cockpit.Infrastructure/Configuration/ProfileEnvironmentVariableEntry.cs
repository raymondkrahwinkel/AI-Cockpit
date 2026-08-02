using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of one profile environment variable (AC-22). A secret value is stored under
// `SecretValue` — a field name the secret rule recognises (`SecretFields`), so it is
// encrypted at rest and scrubbed from backups without this entry knowing how. A plain value stays readable
// in `cockpit.json` under `Value`, on purpose: the config file is the operator's to inspect.
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

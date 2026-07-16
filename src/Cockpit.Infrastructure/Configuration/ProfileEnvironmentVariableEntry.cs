using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of one profile environment variable (AC-22). A secret value is stored under
/// <see cref="SecretValue"/> — a field name the secret rule recognises (<c>SecretFields</c>), so it is
/// encrypted at rest and scrubbed from backups without this entry knowing how. A plain value stays readable
/// in <c>cockpit.json</c> under <see cref="Value"/>, on purpose: the config file is the operator's to inspect.
/// </summary>
internal sealed class ProfileEnvironmentVariableEntry
{
    public string Key { get; set; } = string.Empty;

    /// <summary>The value when it is not a credential.</summary>
    public string? Value { get; set; }

    /// <summary>The value when it is a credential; the field's name is what routes it through encryption.</summary>
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

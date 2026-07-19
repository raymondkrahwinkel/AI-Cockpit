using Cockpit.Core.UsagePill;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="UsagePillSettings"/> in the <c>usagePill</c> section of <c>cockpit.json</c>.
/// Fields are stored by name so the file stays readable and survives the enum being reordered.
/// </summary>
internal sealed class UsagePillSettingsEntry
{
    public List<string> VisibleFields { get; set; } = [];

    public static UsagePillSettingsEntry FromDomain(UsagePillSettings settings) => new()
    {
        VisibleFields = settings.VisibleFields.Select(field => field.ToString()).ToList(),
    };

    public UsagePillSettings ToDomain() => new()
    {
        // A name this build no longer knows (a field removed since the file was written) is dropped rather than
        // throwing, so an older build still loads a config a newer one wrote.
        VisibleFields = VisibleFields
            .Select(name => Enum.TryParse<UsagePillField>(name, out var field) ? field : (UsagePillField?)null)
            .Where(field => field is not null)
            .Select(field => field!.Value)
            .ToList(),
    };
}

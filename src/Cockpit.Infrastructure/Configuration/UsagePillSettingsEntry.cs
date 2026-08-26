using Cockpit.Core.UsagePill;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `UsagePillSettings` in the `usagePill` section of `cockpit.json`.
// Fields are stored by name so the file stays readable and survives the enum being reordered.
internal sealed class UsagePillSettingsEntry
{
    public List<string> VisibleFields { get; set; } = [];

    public static UsagePillSettingsEntry FromDomain(UsagePillSettings settings) => new()
    {
        VisibleFields = settings.VisibleFields.Select(field => field.ToString()).ToList(),
    };

    public UsagePillSettings ToDomain() => new()
    {
        // An unknown name (a field removed since the file was written) is dropped rather than throwing. #1105
        // A2 folds the two old window toggles onto one first — Distinct() keeps that from becoming a duplicate.
        VisibleFields = VisibleFields
            .Select(_MigrateName)
            .Select(name => Enum.TryParse<UsagePillField>(name, out var field) ? field : (UsagePillField?)null)
            .Where(field => field is not null)
            .Select(field => field!.Value)
            .Distinct()
            .ToList(),
    };

    // #1105: pre-migration configs stored "5-hour window" and "Weekly window" as two separate fields; both now
    // draw from the one provider-neutral RateWindows field (A2), so either name folds onto it.
    private static string _MigrateName(string name) => name switch
    {
        "FiveHourWindow" or "WeeklyWindow" => nameof(UsagePillField.RateWindows),
        _ => name,
    };
}

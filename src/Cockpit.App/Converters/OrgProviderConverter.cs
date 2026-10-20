using System.Globalization;
using Avalonia.Data.Converters;
using Cockpit.Core.Profiles;
using Cockpit.Core.Provisioning;

namespace Cockpit.App.Converters;

// AC-1593: a profile outside the organisation's allowed providers, in a picker — bound to [profile, policy]. `Allowed`
// for an item's IsEnabled, `Label` for the "Not allowed by …" beside it; the profile stays listed either way.
public sealed class OrgProviderConverter(bool label) : IMultiValueConverter
{
    public static readonly OrgProviderConverter Allowed = new(false);

    public static readonly OrgProviderConverter Label = new(true);

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var policy = values.Count > 1 ? values[1] as ProvisionPolicy : null;
        var refused = values.Count > 0 && values[0] is SessionProfile profile && policy is not null
            && OrgProviderPolicy.Refusal(policy, profile, OrgProviderPolicy.ModelOf(profile)) is not null;
        return label ? (refused && policy is not null ? OrgProviderPolicy.NotAllowedLabel(policy) : null) : !refused;
    }
}

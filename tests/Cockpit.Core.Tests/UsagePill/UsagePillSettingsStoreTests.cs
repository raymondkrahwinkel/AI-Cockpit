using Cockpit.Core.UsagePill;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Core.Tests.UsagePill;

public class UsagePillSettingsStoreTests
{
    /// <summary>
    /// #1105 A2: the two Claude-specific window toggles ("5-hour window"/"Weekly window") folded into one
    /// provider-neutral RateWindows field. A config saved before that change must not lose the operator's
    /// selection, and having both old names present must not produce two copies of the new one.
    /// </summary>
    [Fact]
    public void Entry_ToDomain_MigratesFiveHourAndWeeklyWindow_ToASingleRateWindows()
    {
        var entry = new UsagePillSettingsEntry { VisibleFields = ["Context", "FiveHourWindow", "WeeklyWindow"] };

        Assert.Equal(new[] { UsagePillField.Context, UsagePillField.RateWindows }, entry.ToDomain().VisibleFields);
    }
}

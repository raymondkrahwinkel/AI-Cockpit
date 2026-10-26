using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Cockpit.Plugin.Productivity.Contracts;
using ModelContextProtocol.Server;

namespace Cockpit.Plugin.Productivity;

internal sealed class ProductivityMcpTools(HoursResultReporter reporter)
{
    [McpServerTool(Name = "report_hours_result", ReadOnly = false, Destructive = false)]
    [Description("Report the result of an offered hours proposal. Only its transport-verified recipient may report. Use the exact markers from the prompt, booked or skipped per item, and a reason for each skipped item. alreadyBooked is the number of minutes already present per day before this proposal; include it even for skipped days. Actual bookings above the saved schedule are shown as red overruns, never as a green success. This tool records results; it does not book hours.")]
    public async Task<string> ReportHoursResult(
        [Description("The ISO Monday date of the offered week, yyyy-MM-dd (for example 2026-10-19). Include the year.")] string week,
        [Description("Results with marker, status (booked or skipped), and reason when skipped.")] HoursResultItem[] items,
        [Description("Already booked minutes per day, with day as yyyy-MM-dd and minutes as a non-negative integer.")] HoursAlreadyBooked[]? alreadyBooked = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!DateOnly.TryParseExact(week, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monday))
            {
                throw new ArgumentException("Geef week op als de maandag in yyyy-MM-dd.");
            }

            var report = await reporter.ReportAsync(new HoursResultRequest(monday, items, alreadyBooked ?? []), cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { ok = true, report.BookedCount, report.Warnings, report.IsGreen }, ProductivityChannel.Json);
        }
        catch (Exception exception) when (exception is ArgumentException or UnauthorizedAccessException or InvalidOperationException)
        {
            return JsonSerializer.Serialize(new { ok = false, error = exception.Message }, ProductivityChannel.Json);
        }
    }
}

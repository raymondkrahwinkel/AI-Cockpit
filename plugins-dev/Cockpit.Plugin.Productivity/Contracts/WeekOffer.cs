using System.Globalization;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

internal static class WeekOfferMarker
{
    public static string Format(int weekNumber, DateOnly day, int sequence) =>
        $"[cockpit:wk{weekNumber}-{_Day(day)}-{sequence}]";

    private static string _Day(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Monday => "Mon",
        DayOfWeek.Tuesday => "Tue",
        DayOfWeek.Wednesday => "Wed",
        DayOfWeek.Thursday => "Thu",
        DayOfWeek.Friday => "Fri",
        DayOfWeek.Saturday => "Sat",
        _ => "Sun",
    };
}

internal sealed record WeekOfferProjectCell(
    DateOnly Day,
    string ProjectId,
    int Minutes,
    string Description,
    string Marker);

internal sealed record FrozenProjectCell(
    DateOnly Day,
    string ProjectId,
    int Minutes,
    string Description,
    string Marker,
    DateTimeOffset OfferedAt,
    string PaneId,
    CockpitTimeEntryStatus Status = CockpitTimeEntryStatus.Offered,
    string? OutcomeReason = null)
{
    public static string StorageKey(DateOnly weekStart) => $"week-offers.{weekStart:yyyy-MM-dd}";

    public ProposalCell ToProposalCell() =>
        new(Day, TimeSpan.FromMinutes(Minutes), Status, ProjectId, OfferedAt, OutcomeReason, Description);
}

internal sealed record WeekOfferDraft(
    string Prompt,
    IReadOnlyList<Guid> OwnEntryIds,
    IReadOnlyList<WeekOfferProjectCell> ProjectCells,
    IReadOnlyList<WeekOfferRow> Rows,
    IReadOnlyDictionary<DateOnly, int> Schedule);

internal static class WeekOfferPrompt
{
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public const string HardBoundary = "Never book more than the schedule per day, including what is already booked. If a day comes above it, or already holds more than the schedule: book nothing for that day and put it forward first.";
    public const string PriorApproval = "First make your own booking proposal (per row: date, duration, contact or project, description; per day the total against the schedule), put it forward and ask for explicit approval. Write nothing before you have that approval.";
    public const string DefaultInstructions = "First read what is already booked this week, including entries without a contact.\nWork out how to book each of your own rows from its description.\nNever book the same thing twice, and keep each row's marker unchanged in the description.\nThen report the result per row with `report_hours_result`, also for rows you did not book; give the reason.";

    public static WeekOfferDraft Build(WeekProposal proposal, int weekNumber, string instructions)
    {
        var rows = proposal.OpenLines
            .SelectMany((line, lineIndex) => line.Cells.Select(cell => (Line: line, Cell: cell, LineIndex: lineIndex)))
            .OrderBy(row => row.Cell.Day)
            .ThenBy(row => row.LineIndex)
            .ToList();
        var sequences = new Dictionary<DateOnly, int>();
        var ownIds = new List<Guid>();
        var projectCells = new List<WeekOfferProjectCell>();
        var offeredRows = new List<WeekOfferRow>();
        var lines = new List<string>
        {
            $"Book this hours proposal for week {weekNumber}.",
            string.Empty,
            "Proposal",
            "| Date | Duration | Project | Description | Type |",
            "| --- | --- | --- | --- | --- |",
        };
        foreach (var row in rows)
        {
            var previous = proposal.Report.Rows.Where(offered => offered.Row.Day == row.Cell.Day).Select(offered => offered.Row.Sequence).DefaultIfEmpty().Max();
            var sequence = Math.Max(sequences.GetValueOrDefault(row.Cell.Day), previous) + 1;
            sequences[row.Cell.Day] = sequence;
            var marker = WeekOfferMarker.Format(weekNumber, row.Cell.Day, sequence);
            offeredRows.Add(new WeekOfferRow(row.Cell.Day, sequence, (int)Math.Round(row.Cell.Time.TotalMinutes), row.Line.EntryId, row.Line.ProjectId));
            var description = row.Line.Own ? row.Line.Label : row.Cell.Description ?? row.Line.Detail;
            var project = row.Line.Own ? row.Line.Detail : row.Line.Label;
            lines.Add($"| {_Date(row.Cell.Day)} | {WeekProposal.Text(row.Cell.Time)} | {_Text(project)} | {_Text(description)} {marker} | {(row.Line.Own ? "own" : string.Empty)} |");
            if (row.Line.EntryId is { } entryId)
            {
                ownIds.Add(entryId);
            }
            else if (row.Line.ProjectId is { } projectId)
            {
                projectCells.Add(new WeekOfferProjectCell(
                    row.Cell.Day,
                    projectId,
                    (int)Math.Round(row.Cell.Time.TotalMinutes),
                    description,
                    marker));
            }
        }

        lines.AddRange([
            string.Empty,
            "Schedule (hard limit)",
            "| Date | Schedule |",
            "| --- | --- |",
            .. proposal.VisibleDays.Select(day => $"| {_Date(day.Day)} | {WeekProposal.Text(day.Schedule)} |"),
            string.Empty,
            HardBoundary,
            string.Empty,
            PriorApproval,
        ]);
        if (string.IsNullOrWhiteSpace(instructions) == false)
        {
            lines.AddRange([string.Empty, "Instructions", instructions.Trim()]);
        }

        return new WeekOfferDraft(
            string.Join('\n', lines),
            [.. ownIds.Distinct()],
            projectCells,
            offeredRows,
            proposal.Days.ToDictionary(day => day.Day, day => (int)day.Schedule.TotalMinutes));
    }

    private static string _Date(DateOnly day) =>
        day.ToDateTime(TimeOnly.MinValue).ToString("ddd dd-MM-yyyy", English);

    private static string _Text(string value) =>
        value.ReplaceLineEndings(" ").Replace("|", "\\|", StringComparison.Ordinal).Trim();
}

internal enum WeekOfferDestinationKind
{
    Session,
    Profile,
}

internal sealed record WeekOfferDestination(WeekOfferDestinationKind Kind, string Id, string Label)
{
    public static WeekOfferDestination Session(string paneId, string label) =>
        new(WeekOfferDestinationKind.Session, paneId, label);

    public static WeekOfferDestination Profile(string profileLabel) =>
        new(WeekOfferDestinationKind.Profile, profileLabel, profileLabel);

    public static string StorageKey(string? projectId) =>
        $"week-offer.destination.{(string.IsNullOrWhiteSpace(projectId) ? "none" : projectId)}";
}

internal sealed record WeekOfferSendRequest(
    WeekOfferDestination Destination,
    string Prompt,
    DateOnly WeekStart,
    int WeekNumber,
    IReadOnlyList<Guid> OwnEntryIds,
    IReadOnlyList<WeekOfferProjectCell> ProjectCells,
    string? ProjectId,
    IReadOnlyList<WeekOfferRow> Rows,
    IReadOnlyDictionary<DateOnly, int> Schedule);

internal sealed record WeekOfferSendResult(string PaneId, string? FinalizationError = null);

internal sealed class WeekOfferFinalizationException(string paneId, Exception innerException)
    : Exception("The hours proposal was delivered, but the status could not fully be updated. Do not send it again; close this window and check the rows.", innerException)
{
    public string PaneId { get; } = paneId;
}

internal sealed class WeekOfferSender(
    ICockpitActions actions,
    ICockpitSessionObserver sessions,
    ICockpitTimeEntries timeEntries,
    IPluginStorage storage,
    TimeProvider time,
    SemaphoreSlim reportGate)
{
    public async Task<WeekOfferSendResult> SendAsync(WeekOfferSendRequest request)
    {
        await reportGate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await _SendAsync(request).ConfigureAwait(false);
        }
        finally
        {
            reportGate.Release();
        }
    }

    private async Task<WeekOfferSendResult> _SendAsync(WeekOfferSendRequest request)
    {
        var paneId = request.Destination.Kind switch
        {
            WeekOfferDestinationKind.Session => await _SendToSessionAsync(request).ConfigureAwait(false),
            WeekOfferDestinationKind.Profile => await _StartSessionAsync(request).ConfigureAwait(false),
            _ => throw new InvalidOperationException("Choose a session or profile."),
        };
        try
        {
            var offeredAt = time.GetUtcNow();
            if (request.OwnEntryIds.Count > 0)
            {
                await timeEntries.MarkOfferedAsync(request.OwnEntryIds, offeredAt, paneId).ConfigureAwait(false);
            }

            _FreezeProjectCells(request, offeredAt, paneId);
            var key = WeekReport.StorageKey(request.WeekStart);
            var report = storage.Get<WeekReport>(key) ?? WeekReport.Empty;
            var retained = report.Rows.Where(old => request.Rows.All(row => row.EntryId is not null
                ? row.EntryId != old.Row.EntryId
                : old.Row.EntryId is not null || row.Day != old.Row.Day || row.ProjectId != old.Row.ProjectId));
            var offerId = Guid.NewGuid();
            storage.Set(key, report with { Rows = [.. retained, .. request.Rows.Select(row => new WeekReportedRow(row, paneId, request.Schedule[row.Day], OfferId: offerId,
                KnownBookedAtOffer: report.Rows.Where(previous => previous.Row.Day == row.Day && previous.Status == "booked").Sum(previous => previous.Row.Minutes)))] });
            storage.Set(WeekOfferDestination.StorageKey(request.ProjectId), request.Destination);
        }
        catch (Exception exception)
        {
            return new WeekOfferSendResult(paneId, exception.Message);
        }

        return new WeekOfferSendResult(paneId);
    }

    private async Task<string> _SendToSessionAsync(WeekOfferSendRequest request)
    {
        await actions.SendPromptToSessionAsync(
            request.Destination.Id,
            request.Prompt,
            $"Hours proposal week {request.WeekNumber}").ConfigureAwait(false);
        return request.Destination.Id;
    }

    private async Task<string> _StartSessionAsync(WeekOfferSendRequest request)
    {
        var existing = sessions.OpenSessions.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        var sessionName = $"Hours week {request.WeekNumber} · {Guid.NewGuid():N}"[..22];
        await actions.StartSessionAsync(request.Destination.Id, request.Prompt, null, sessionName).ConfigureAwait(false);
        var matches = sessions.OpenSessions
            .Where(session => existing.Contains(session.PaneId) == false && string.Equals(session.Name, sessionName, StringComparison.Ordinal))
            .ToList();
        return matches.Count == 1
            ? matches[0].PaneId
            : throw new InvalidOperationException("The new session has started, but Cockpit could not find the recipient unambiguously. The rows stay open.");
    }

    private void _FreezeProjectCells(WeekOfferSendRequest request, DateTimeOffset offeredAt, string paneId)
    {
        if (request.ProjectCells.Count == 0)
        {
            return;
        }

        var key = FrozenProjectCell.StorageKey(request.WeekStart);
        var replacements = request.ProjectCells.Select(cell => (cell.Day, cell.ProjectId)).ToHashSet();
        var existing = storage.Get<FrozenProjectCell[]>(key) ?? [];
        var added = request.ProjectCells.Select(cell => new FrozenProjectCell(
            cell.Day,
            cell.ProjectId,
            cell.Minutes,
            cell.Description,
            cell.Marker,
            offeredAt,
            paneId));
        storage.Set<FrozenProjectCell[]>(key, [.. existing.Where(cell => replacements.Contains((cell.Day, cell.ProjectId)) == false), .. added]);
    }
}

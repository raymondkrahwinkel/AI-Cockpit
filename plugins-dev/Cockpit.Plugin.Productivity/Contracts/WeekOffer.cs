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
        DayOfWeek.Monday => "ma",
        DayOfWeek.Tuesday => "di",
        DayOfWeek.Wednesday => "wo",
        DayOfWeek.Thursday => "do",
        DayOfWeek.Friday => "vr",
        DayOfWeek.Saturday => "za",
        _ => "zo",
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
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");

    public const string HardBoundary = "Boek per dag nooit meer dan het schema, met wat er al geboekt staat meegerekend. Komt een dag erboven, of staat er al meer dan het schema: boek die dag niets en leg hem eerst voor.";
    public const string PriorApproval = "Maak eerst je eigen boekingsvoorstel (per regel datum, duur, contact of project, omschrijving; per dag het totaal tegenover het schema), leg het voor en vraag expliciet akkoord. Schrijf niets voordat je dat akkoord hebt.";
    public const string DefaultInstructions = "Lees eerst wat er deze week al geboekt staat, ook zonder contact.\nLeid uit de omschrijving van een eigen regel af hoe je die boekt.\nBoek niets dubbel en zet het kenmerk uit elke regel ongewijzigd in de omschrijving.\nMeld daarna per regel het resultaat met `report_hours_result`, ook als je een regel niet boekte; geef dan de reden.";

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
            $"Boek dit urenvoorstel voor week {weekNumber}.",
            string.Empty,
            "Voorstel",
            "| Datum | Duur | Project | Omschrijving | Soort |",
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
            lines.Add($"| {_Date(row.Cell.Day)} | {WeekProposal.Text(row.Cell.Time)} | {_Text(project)} | {_Text(description)} {marker} | {(row.Line.Own ? "eigen" : string.Empty)} |");
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
            "Werkschema (harde grens)",
            "| Datum | Schema |",
            "| --- | --- |",
            .. proposal.VisibleDays.Select(day => $"| {_Date(day.Day)} | {WeekProposal.Text(day.Schedule)} |"),
            string.Empty,
            HardBoundary,
            string.Empty,
            PriorApproval,
        ]);
        if (string.IsNullOrWhiteSpace(instructions) == false)
        {
            lines.AddRange([string.Empty, "Instructies", instructions.Trim()]);
        }

        return new WeekOfferDraft(
            string.Join('\n', lines),
            [.. ownIds.Distinct()],
            projectCells,
            offeredRows,
            proposal.Days.ToDictionary(day => day.Day, day => (int)day.Schedule.TotalMinutes));
    }

    private static string _Date(DateOnly day) =>
        day.ToDateTime(TimeOnly.MinValue).ToString("ddd dd-MM-yyyy", Dutch);

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
    : Exception("Het urenvoorstel is afgeleverd, maar de status kon niet volledig worden bijgewerkt. Verstuur het niet opnieuw; sluit dit venster en controleer de regels.", innerException)
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
            _ => throw new InvalidOperationException("Kies een sessie of profiel."),
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
            storage.Set(key, report with { Rows = [.. retained, .. request.Rows.Select(row => new WeekReportedRow(row, paneId, request.Schedule[row.Day], OfferId: offerId))] });
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
            $"Urenvoorstel week {request.WeekNumber}").ConfigureAwait(false);
        return request.Destination.Id;
    }

    private async Task<string> _StartSessionAsync(WeekOfferSendRequest request)
    {
        var existing = sessions.OpenSessions.Select(session => session.PaneId).ToHashSet(StringComparer.Ordinal);
        var sessionName = $"Uren week {request.WeekNumber} · {Guid.NewGuid():N}"[..22];
        await actions.StartSessionAsync(request.Destination.Id, request.Prompt, null, sessionName).ConfigureAwait(false);
        var matches = sessions.OpenSessions
            .Where(session => existing.Contains(session.PaneId) == false && string.Equals(session.Name, sessionName, StringComparison.Ordinal))
            .ToList();
        return matches.Count == 1
            ? matches[0].PaneId
            : throw new InvalidOperationException("De nieuwe sessie is gestart, maar Cockpit kon de ontvanger niet eenduidig terugvinden. De regels blijven open.");
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

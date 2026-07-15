using Cockpit.Core.Workspaces;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of one <see cref="WorkspacePane"/>. The grid cell is flattened to four numbers rather than a
/// nested object: it reads better in a file an operator may edit by hand, and matches how the pane's position
/// is already talked about (column/row/span).
/// </summary>
internal sealed class WorkspacePaneEntry
{
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = nameof(PaneKind.AiSession);

    public int Column { get; set; }

    public int Row { get; set; }

    public int ColumnSpan { get; set; } = 1;

    public int RowSpan { get; set; } = 1;

    public string? WidgetId { get; set; }

    public string? ProfileId { get; set; }

    public string? Shell { get; set; }

    public string? WorkingDirectory { get; set; }

    public static WorkspacePaneEntry FromDomain(WorkspacePane pane) => new()
    {
        Id = pane.Id,
        Kind = pane.Kind.ToString(),
        Column = pane.Cell.Column,
        Row = pane.Cell.Row,
        ColumnSpan = pane.Cell.ColumnSpan,
        RowSpan = pane.Cell.RowSpan,
        WidgetId = pane.WidgetId,
        ProfileId = pane.ProfileId,
        Shell = pane.Shell,
        WorkingDirectory = pane.WorkingDirectory,
    };

    /// <summary>This entry as a domain record; spans are floored at one so a zero-span pane cannot render as invisible.</summary>
    public WorkspacePane ToDomain()
    {
        var kind = Enum.TryParse<PaneKind>(Kind, ignoreCase: true, out var parsed) ? parsed : PaneKind.AiSession;
        return new WorkspacePane(Id, kind)
        {
            Cell = new GridCell(Column, Row, Math.Max(1, ColumnSpan), Math.Max(1, RowSpan)),
            WidgetId = WidgetId,
            ProfileId = ProfileId,
            Shell = Shell,
            WorkingDirectory = WorkingDirectory,
        };
    }
}

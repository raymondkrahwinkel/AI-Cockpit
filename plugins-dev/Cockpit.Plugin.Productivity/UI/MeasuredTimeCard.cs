using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Productivity.Contracts;

namespace Cockpit.Plugin.Productivity.UI;

// The context column of Uren · Dag (AC-1731): the strip, the measured time per project, your time and the agents.
internal static class MeasuredTimeCard
{
    public const string You = "CockpitAccentBrush";
    public const string Own = "CockpitStatusWaitingBrush";
    public const string Agents = "CockpitStatusBusyBrush";

    public static Control Build(AttentionTime? day, string title)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
        var hint = Ui.Hint("context, niet te bewerken");
        Grid.SetColumn(hint, 1);
        heading.Children.Add(hint);
        var body = new StackPanel { Spacing = 10, Children = { heading } };
        if (day is null || (day.You.Count == 0 && day.OwnEntries.Count == 0 && day.Agents.Count == 0 && day.AgentTime == TimeSpan.Zero))
        {
            body.Children.Add(Ui.Hint("Geen tijdlijndata voor deze dag."));
            return Ui.Card(body, 14);
        }

        body.Children.Add(new TimelineStrip(day) { Name = "TimelineStrip", Height = 58 });
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children = { _Legend(You, "jij, gemeten"), _Legend(Own, "eigen regels"), _Legend(Agents, "agents") },
        });

        var longest = day.MeasuredPerProject.Count == 0 ? 1 : day.MeasuredPerProject.Max(project => project.Time.TotalMinutes);
        foreach (var project in day.MeasuredPerProject)
        {
            body.Children.Add(_ProjectRow(project, longest));
        }

        var measured = day.Measured - day.Overlap;
        body.Children.Add(_Summary("Jouw tijd", Ui.Duration(day.YourTime), $"= gemeten {Ui.Duration(measured)} + eigen {Ui.Duration(day.Own)}"));
        body.Children.Add(_Summary("Agents actief", Ui.Duration(day.AgentTime), "· naast jou, niet erbij"));
        body.Children.Add(Ui.Hint("Regels en gemeten tijd op hetzelfde moment tellen één keer; de regel wint het project."));
        return Ui.Card(body, 14);
    }

    private static Control _Legend(string brush, string text) =>
        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { Ui.Dot(brush), Ui.Hint(text) } };

    private static Control _ProjectRow(ProjectTime project, double longest)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*,50"), ColumnSpacing = 10 };
        row.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            Children = { Ui.Dot(You), new TextBlock { Text = project.ProjectId ?? "geen project", TextTrimming = TextTrimming.CharacterEllipsis } },
        });
        var bar = new Border { Height = 12, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center }
            .Themed(Border.BackgroundProperty, You);
        var share = project.Time.TotalMinutes / longest;
        var track = new Grid();
        track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(share, GridUnitType.Star)));
        track.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(1 - share, GridUnitType.Star)));
        track.Children.Add(bar);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);
        var time = new TextBlock { Text = Ui.Duration(project.Time), HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(time, 2);
        row.Children.Add(time);
        return row;
    }

    private static Control _Summary(string label, string value, string detail)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 6 };
        row.Children.Add(Ui.Hint(label));
        var strong = new TextBlock { Text = value, FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(strong, 1);
        row.Children.Add(strong);
        var rest = Ui.Hint(detail);
        Grid.SetColumn(rest, 2);
        row.Children.Add(rest);
        return new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 0), Child = row }
            .Themed(Border.BorderBrushProperty, "CockpitHairlineBrush");
    }

    // Two lanes over the day's hours: you (measured, own entries on top) and the agents' busy intervals.
    private sealed class TimelineStrip(AttentionTime day) : Control
    {
        private const double Axis = 16;

        public override void Render(DrawingContext context)
        {
            var spans = day.You.Concat(day.OwnEntries).Concat(day.Agents).ToList();
            var date = spans.Count == 0 ? DateTime.Today : spans.Min(span => span.Start).LocalDateTime.Date;
            var first = Math.Min(8, spans.Count == 0 ? 8 : Math.Floor(spans.Min(span => (span.Start.LocalDateTime - date).TotalHours)));
            var last = Math.Max(18, spans.Count == 0 ? 18 : Math.Ceiling(spans.Max(span => (span.End.LocalDateTime - date).TotalHours)));
            var width = Bounds.Width;
            var lane = (Bounds.Height - Axis - 6) / 2;
            double X(DateTimeOffset at) => Math.Clamp(((at.LocalDateTime - date).TotalHours - first) / (last - first), 0, 1) * width;

            var label = _Brush("CockpitTextFaintBrush");
            for (var hour = first; hour <= last; hour += 2)
            {
                var text = new FormattedText(
                    $"{hour:00}:00",
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    Typeface.Default,
                    10.5,
                    label);
                var x = Math.Clamp(((hour - first) / (last - first) * width) - (text.Width / 2), 0, width - text.Width);
                context.DrawText(text, new Point(x, 0));
            }

            var track = _Brush("CockpitInsetBgBrush");
            context.DrawRectangle(track, null, new Rect(0, Axis, width, lane), 4, 4);
            context.DrawRectangle(track, null, new Rect(0, Axis + lane + 6, width, lane), 4, 4);
            void Draw(IEnumerable<AttentionSpan> lanespans, string brush, double top, double inset)
            {
                foreach (var span in lanespans)
                {
                    var left = X(span.Start);
                    context.DrawRectangle(_Brush(brush), null, new Rect(left, top + inset, Math.Max(2, X(span.End) - left), lane - (2 * inset)), 3, 3);
                }
            }

            Draw(day.You, You, Axis, 4);
            Draw(day.OwnEntries, Own, Axis, 4);
            Draw(day.Agents, Agents, Axis + lane + 6, 4);
        }

        private IBrush? _Brush(string key) =>
            this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;
    }
}

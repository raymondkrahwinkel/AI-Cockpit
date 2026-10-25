using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugin.Shared.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class OverviewWorkspaceBody : UserControl
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private static readonly Color Lavender = Color.Parse("#B79CF0");
    private static readonly Color Own = Color.Parse("#E2A93B");
    private static readonly Color Agent = Color.Parse("#22C7DF");

    private readonly OverviewController _controller;
    private readonly StackPanel _body = new() { Spacing = 12, Margin = new Thickness(18, 14) };

    public OverviewWorkspaceBody(OverviewController controller)
    {
        _controller = controller;
        Content = new ScrollViewer { Content = _body };
        _controller.Changed += _Render;
        DetachedFromVisualTree += (_, _) => _controller.Changed -= _Render;
        _Render();
        _ = _controller.RefreshAsync();
    }

    private void _Render()
    {
        _body.Children.Clear();
        _body.Children.Add(_Header());
        if (_controller.Snapshot is not { } snapshot)
        {
            _body.Children.Add(Ui.Card(Ui.Hint(_controller.Error ?? "Overzicht laden…"), 14));
            return;
        }

        _body.Children.Add(_RangeCard());
        _body.Children.Add(_Tiles(snapshot));
        _body.Children.Add(_Charts(snapshot));
        _body.Children.Add(_Entries(snapshot));
        _body.Children.Add(_Tickets(snapshot));
    }

    private Control _Header()
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(new TextBlock
        {
            Text = "Overzicht",
            FontSize = 19,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var today = Ui.Pill($"vandaag · {DateTimeOffset.Now.ToString("ddd d MMM", Dutch)}", "CockpitTextSecondaryBrush", "CockpitInsetBgBrush");
        Grid.SetColumn(today, 1);
        row.Children.Add(today);
        return row;
    }

    private Control _RangeCard()
    {
        var picker = new RangePicker
        {
            Name = "OverviewRangePicker",
            Height = 38,
            SelectedRange = "Werkweek",
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 520,
        };
        var dates = new TextBlock
        {
            Text = $"{_controller.From.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch)} – {_controller.To.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM yyyy", Dutch)}",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var basis = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Hint("Basis:"),
                new Button { Name = "YourTimeOnly", Content = "Jouw tijd", Classes = { "Accent", "Compact" } },
                new Button { Name = "IncludeAgent", Content = "+ agent zonder jou", Classes = { "Compact" } },
            },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 12 };
        row.Children.Add(picker);
        Grid.SetColumn(dates, 2);
        row.Children.Add(dates);
        Grid.SetColumn(basis, 3);
        row.Children.Add(basis);
        return Ui.Card(row, 10);
    }

    private static Control _Tiles(ProductivityOverviewSnapshot snapshot)
    {
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        var measured = snapshot.Attention.Measured - snapshot.Attention.Overlap;
        var sessions = snapshot.Attention.Agents.Select(span => span.Owner).Where(owner => owner is not null).Distinct(StringComparer.Ordinal).Count();
        var cost = snapshot.Timeline.Usage.Sum(usage => usage.CostUsd) + snapshot.Timeline.Days.Sum(day => day.ReportedCostUsd);
        var schedule = snapshot.ScheduleMinutes.Sum();
        var values = new[]
        {
            new StatTile
            {
                Name = "YourTimeTile",
                Label = "Jouw tijd",
                Value = Ui.Duration(snapshot.YourTime),
                Detail = $"gemeten {Ui.Duration(measured)} + eigen {Ui.Duration(snapshot.Attention.Own)} − {Ui.Duration(snapshot.Attention.Overlap)} overlap",
            },
            new StatTile
            {
                Name = "AgentTimeTile",
                Label = "Agents (actief)",
                Value = Ui.Duration(snapshot.Attention.AgentTime),
                Detail = $"{sessions} sessies · naast jouw tijd",
            },
            new StatTile
            {
                Name = "ScheduleTile",
                Label = "Schema",
                Value = schedule == 0 ? "—" : Ui.Duration(TimeSpan.FromMinutes(schedule)),
                Detail = schedule == 0 ? "geen schema ingesteld" : _ScheduleDetail(snapshot.ScheduleMinutes),
            },
            new StatTile
            {
                Name = "CostTile",
                Label = "Kosten",
                Value = cost.ToString("$0.00", CultureInfo.InvariantCulture),
                Detail = $"{cost.ToString("$0.##", CultureInfo.InvariantCulture)} gemeld",
            },
            new StatTile
            {
                Name = "OwnEntriesTile",
                Label = "Eigen regels",
                Value = snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture),
                Detail = $"{snapshot.Entries.Count(entry => entry.Status == Cockpit.Plugins.Abstractions.TimeTracking.CockpitTimeEntryStatus.Open)} open",
            },
        };
        for (var index = 0; index < values.Length; index++)
        {
            values[index].Height = 96;
            Grid.SetColumn(values[index], index);
            tiles.Children.Add(values[index]);
        }

        return tiles;
    }

    private static string _ScheduleDetail(IReadOnlyList<int> minutes)
    {
        var labels = new[] { "ma", "di", "wo", "do", "vr", "za", "zo" };
        return string.Join(" · ", minutes.Take(7).Select((value, index) => $"{labels[index]} {Ui.Duration(TimeSpan.FromMinutes(value))}"));
    }

    private static Control _Charts(ProductivityOverviewSnapshot snapshot)
    {
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,2*"), ColumnSpacing = 12 };
        columns.Children.Add(_DailyChart(snapshot));
        var right = new StackPanel { Spacing = 12, Children = { _Projects(snapshot), _Sessions(snapshot) } };
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        return columns;
    }

    private static Control _DailyChart(ProductivityOverviewSnapshot snapshot)
    {
        var chart = new GroupedBarChartControl
        {
            Height = 280,
            FirstColor = Lavender,
            SecondColor = Agent,
            Items =
            [
                .. snapshot.Days.Select(day =>
                {
                    var measured = day.Attention.Measured - day.Attention.Overlap;
                    return new GroupedBarDatum(
                        day.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch),
                        day.Attention.YourTime.TotalHours,
                        day.Attention.AgentTime.TotalHours,
                        [
                            new ChartSegment("jouw tijd · gemeten", measured.TotalHours, Lavender),
                            new ChartSegment("jouw tijd · eigen regels", day.Attention.Own.TotalHours, Own, true),
                        ]);
                }),
            ],
            TooltipText = "Jouw tijd en actieve agenttijd per dag",
        };
        return Ui.Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Jouw tijd per dag, met de agents ernaast", FontSize = 15, FontWeight = FontWeight.SemiBold },
                chart,
                _Legend(),
                Ui.Hint("Per dag twee staven: links jouw tijd, rechts de agents. Agenttijd telt nooit op bij jouw tijd."),
            },
        }, 14);
    }

    private static Control _Legend() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                _LegendItem(Lavender, "jouw tijd · gemeten"),
                _LegendItem(Own, "jouw tijd · eigen regels"),
                _LegendItem(Agent, "agents actief"),
            },
        };

    private static Control _LegendItem(Color color, string label) =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children =
            {
                new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(color), VerticalAlignment = VerticalAlignment.Center },
                Ui.Hint(label),
            },
        };

    private static Control _Projects(ProductivityOverviewSnapshot snapshot)
    {
        var body = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = "Tijd per project", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _Legend(),
            },
        };
        foreach (var project in _ProjectRows(snapshot))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,52"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = project.Label, TextTrimming = TextTrimming.CharacterEllipsis });
            var chart = new HorizontalBarChartControl
            {
                Height = 34,
                Segments =
                [
                    new("gemeten", project.Measured.TotalMinutes, Lavender),
                    new("eigen regels", project.Own.TotalMinutes, Own, true),
                ],
                TooltipText = project.Label,
            };
            Grid.SetColumn(chart, 1);
            row.Children.Add(chart);
            var total = new TextBlock
            {
                Text = Ui.Duration(project.Measured + project.Own),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontWeight = FontWeight.SemiBold,
            };
            Grid.SetColumn(total, 2);
            row.Children.Add(total);
            body.Children.Add(row);
        }

        return Ui.Card(body, 14);
    }

    private static IReadOnlyList<_ProjectRow> _ProjectRows(ProductivityOverviewSnapshot snapshot)
    {
        const string none = "";
        var names = snapshot.Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var measured = snapshot.Attention.MeasuredPerProject.ToDictionary(project => project.ProjectId ?? none, project => project.Time, StringComparer.Ordinal);
        var own = snapshot.Entries
            .GroupBy(entry => entry.ProjectId ?? none, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TimeSpan.FromTicks(group.Sum(entry => entry.Duration.Ticks)), StringComparer.Ordinal);
        return measured.Keys.Concat(own.Keys).Distinct(StringComparer.Ordinal)
            .Select(id => new _ProjectRow(
                id.Length == 0 ? "geen project" : names.GetValueOrDefault(id, id),
                measured.GetValueOrDefault(id),
                own.GetValueOrDefault(id)))
            .OrderByDescending(row => row.Measured + row.Own)
            .ToList();
    }

    private static Control _Sessions(ProductivityOverviewSnapshot snapshot)
    {
        var body = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = "Agents", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _TableHead(["Sessie", "Profiel", "Actief", "Zonder jou", "Tokens", "Kosten"], "2*,*,70,80,80,80"),
            },
        };
        var active = snapshot.Attention.Agents
            .GroupBy(span => span.Owner ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TimeSpan.FromTicks(group.Sum(span => span.Duration.Ticks)), StringComparer.Ordinal);
        foreach (var usage in snapshot.Timeline.Usage.GroupBy(value => value.PaneId, StringComparer.Ordinal))
        {
            var tokens = usage.Sum(value => (long)value.InputTokens + value.OutputTokens + value.CacheReadInputTokens + value.CacheCreationInputTokens);
            var cost = usage.Sum(value => value.CostUsd);
            body.Children.Add(_TableRow(
                [
                    usage.Key,
                    usage.Select(value => value.Model).LastOrDefault(model => !string.IsNullOrWhiteSpace(model)) ?? "onbekend",
                    Ui.Duration(active.GetValueOrDefault(usage.Key)),
                    Ui.Duration(snapshot.Attention.AgentWithoutYouPerSession.GetValueOrDefault(usage.Key)),
                    _Tokens(tokens),
                    cost == 0 && tokens > 0 ? "geen prijs" : cost.ToString("$0.00", CultureInfo.InvariantCulture),
                ],
                "2*,*,70,80,80,80"));
        }

        return Ui.Card(body, 14);
    }

    private static string _Tokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000d:0.#}k" : tokens.ToString(CultureInfo.InvariantCulture);

    private static Control _Entries(ProductivityOverviewSnapshot snapshot)
    {
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Eigen regels deze week", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _TableHead(["Dag", "Omschrijving", "Project", "Duur", "Status"], "80,*,160,70,90"),
            },
        };
        foreach (var entry in snapshot.Entries.OrderBy(entry => entry.StartedAt))
        {
            body.Children.Add(_TableRow(
                [
                    entry.StartedAt.ToLocalTime().ToString("ddd d", Dutch),
                    entry.Description,
                    entry.ProjectId ?? "geen project",
                    Ui.Duration(entry.Duration),
                    Ui.Status(entry),
                ],
                "80,*,160,70,90"));
        }

        return Ui.Card(body, 14);
    }

    private static Control _Tickets(ProductivityOverviewSnapshot snapshot)
    {
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Tickets in dit bereik", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _TableHead(["Ticket", "Project", "Jouw tijd", "Laatste status"], "120,180,90,*"),
            },
        };
        foreach (var ticket in snapshot.Tickets)
        {
            body.Children.Add(_TableRow(
                [ticket.Ticket, ticket.ProjectId ?? "geen project", Ui.Duration(ticket.Time), ticket.Statusline ?? "geen statusline"],
                "120,180,90,*"));
        }

        if (snapshot.Tickets.Count == 0)
        {
            body.Children.Add(Ui.Hint("Geen tickets in dit bereik."));
        }

        return Ui.Card(body, 14);
    }

    private static Control _TableHead(IReadOnlyList<string> cells, string columns)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 8 };
        for (var index = 0; index < cells.Count; index++)
        {
            var value = Ui.Hint(cells[index]);
            Grid.SetColumn(value, index);
            row.Children.Add(value);
        }

        return row;
    }

    private static Control _TableRow(IReadOnlyList<string> cells, string columns)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 8 };
        for (var index = 0; index < cells.Count; index++)
        {
            var value = new TextBlock { Text = cells[index], FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(value, index);
            row.Children.Add(value);
        }

        return new Border
        {
            Child = row,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 5),
        }.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");
    }

    private sealed record _ProjectRow(string Label, TimeSpan Measured, TimeSpan Own);
}

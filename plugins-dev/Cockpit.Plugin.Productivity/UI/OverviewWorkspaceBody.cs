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
    private readonly Action _openHours;
    private readonly StackPanel _body = new() { Spacing = 12, Margin = new Thickness(18, 14) };

    public OverviewWorkspaceBody(OverviewController controller, Action openHours)
    {
        _controller = controller;
        _openHours = openHours;
        Content = new ScrollViewer { Content = _body };
        _controller.Changed += _Render;
        DetachedFromVisualTree += (_, _) => _controller.Changed -= _Render;
        _Render();
        _ = _controller.RefreshAsync();
    }

    private void _Render()
    {
        _body.Children.Clear();
        _body.Children.Add(_Tabs());
        _body.Children.Add(_RangeCard());
        if (_controller.Error is { } error)
        {
            _body.Children.Add(Ui.Card(Ui.Hint(error), 14));
            return;
        }

        if (_controller.Snapshot is not { } snapshot)
        {
            _body.Children.Add(Ui.Card(Ui.Hint(_controller.Error ?? "Overzicht laden…"), 14));
            return;
        }

        _body.Children.Add(_Tiles(snapshot, _controller.IncludeAgentWithoutYou, _controller.From, _controller.To));
        _body.Children.Add(_Charts(snapshot, _controller.IncludeAgentWithoutYou));
        _body.Children.Add(_Entries(snapshot));
        _body.Children.Add(_Tickets(snapshot));
    }

    private Control _Tabs()
    {
        var hours = new Button { Name = "OpenHoursTab", Content = "Uren", Classes = { "Compact" } };
        hours.Click += (_, _) => _openHours();
        var overview = new Button { Name = "OverviewTab", Content = "Overzicht", Classes = { "Compact", "Accent" } };
        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { hours, overview },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(tabs);
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
            Ranges = ["Dag", "Werkweek", "Week", "Maand", "Eigen bereik"],
            SelectedRange = _controller.SelectedRange,
            Width = 520,
        };
        picker.SelectedRangeChanged += async (_, range) => await _controller.SelectRangeAsync(range);
        var pickerClip = new Grid
        {
            Width = 520,
            Height = 38,
        };
        pickerClip.Children.Add(picker);
        var leftCover = new Border { Width = 32, HorizontalAlignment = HorizontalAlignment.Left };
        leftCover.Themed(BackgroundProperty, "CockpitPanelBgBrush");
        pickerClip.Children.Add(leftCover);
        var rightCover = new Border { Width = 210, HorizontalAlignment = HorizontalAlignment.Right };
        rightCover.Themed(BackgroundProperty, "CockpitPanelBgBrush");
        pickerClip.Children.Add(rightCover);
        var dates = new TextBlock
        {
            Text = $"{_controller.From.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch)} – {_controller.To.ToDateTime(TimeOnly.MinValue).ToString("ddd d MMM yyyy", Dutch)}",
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previous = new Button { Name = "PreviousOverviewRange", Content = "‹", Classes = { "Compact" } };
        previous.Click += async (_, _) => await _controller.MoveAsync(-1);
        var next = new Button { Name = "NextOverviewRange", Content = "›", Classes = { "Compact" } };
        next.Click += async (_, _) => await _controller.MoveAsync(1);
        var dateNavigation = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { previous, dates, next },
        };
        var yours = new Button { Name = "YourTimeOnly", Content = "Jouw tijd", Classes = { "Compact" } };
        var withAgent = new Button { Name = "IncludeAgent", Content = "+ agent zonder jou", Classes = { "Compact" } };
        if (_controller.IncludeAgentWithoutYou)
        {
            withAgent.Classes.Add("Accent");
        }
        else
        {
            yours.Classes.Add("Accent");
        }

        yours.Click += (_, _) => _controller.SetIncludeAgentWithoutYou(false);
        withAgent.Click += (_, _) => _controller.SetIncludeAgentWithoutYou(true);
        var basis = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Hint("Basis:"),
                yours,
                withAgent,
            },
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        row.Children.Add(pickerClip);
        Grid.SetColumn(dateNavigation, 1);
        row.Children.Add(dateNavigation);
        Grid.SetColumn(basis, 2);
        row.Children.Add(basis);
        var customFrom = new DatePicker
        {
            Name = "CustomFrom",
            SelectedDate = _controller.From.ToDateTime(TimeOnly.MinValue),
            Width = 150,
        };
        var customTo = new DatePicker
        {
            Name = "CustomTo",
            SelectedDate = _controller.To.ToDateTime(TimeOnly.MinValue),
            Width = 150,
        };
        var apply = new Button { Name = "ApplyCustomRange", Content = "Toepassen", Classes = { "Accent", "Compact" } };
        apply.Click += async (_, _) =>
        {
            if (customFrom.SelectedDate is { } from && customTo.SelectedDate is { } to)
            {
                await _controller.SetCustomRangeAsync(DateOnly.FromDateTime(from.Date), DateOnly.FromDateTime(to.Date));
            }
            else
            {
                _controller.ReportMissingCustomRange();
            }
        };
        var custom = new StackPanel
        {
            IsVisible = _controller.SelectedRange == "Eigen bereik",
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { Ui.Hint("Van"), customFrom, Ui.Hint("tot"), customTo, apply },
        };
        return Ui.Card(new StackPanel { Spacing = 8, Children = { row, custom } }, 10);
    }

    private static Control _Tiles(ProductivityOverviewSnapshot snapshot, bool includeAgent, DateOnly from, DateOnly to)
    {
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*"), ColumnSpacing = 10 };
        var historicalMeasured = TimeSpan.FromTicks(snapshot.HistoricalMeasuredPerProject.Sum(project => project.Time.Ticks));
        var measured = snapshot.Attention.Measured - snapshot.Attention.Overlap + historicalMeasured;
        var sessions = snapshot.Attention.Agents.Select(span => span.Owner)
            .Concat(snapshot.Timeline.Days.Select(day => day.PaneId))
            .Where(owner => owner is not null)
            .Distinct(StringComparer.Ordinal)
            .Count();
        var cost = snapshot.Timeline.Usage.Sum(usage => usage.CostUsd) + snapshot.Timeline.Days.Sum(day => day.ReportedCostUsd);
        var tokens = snapshot.Timeline.Usage.Sum(usage => (long)usage.InputTokens + usage.OutputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens)
            + snapshot.Timeline.Days.Sum(day => (long)day.InputTokens + day.OutputTokens + day.CacheReadInputTokens + day.CacheCreationInputTokens);
        var hasUnpriced = snapshot.Timeline.Usage.Any(usage => _TokenCount(usage) > 0 && usage.CostUsd == 0)
            || snapshot.Timeline.Days.Any(day => _TokenCount(day) > 0 && day.ReportedCostUsd == 0);
        var costText = hasUnpriced ? "geen prijs" : cost.ToString("$0.00", CultureInfo.InvariantCulture);
        var schedule = _ScheduledMinutes(snapshot.ScheduleMinutes, from, to);
        var values = new[]
        {
            ("YourTimeTile", "Jouw tijd", Ui.Duration(includeAgent ? snapshot.WithAgentWithoutYou : snapshot.YourTime), $"gemeten {Ui.Duration(measured)} + eigen {Ui.Duration(snapshot.Attention.Own)} − {Ui.Duration(snapshot.Attention.Overlap)} overlap"),
            ("AgentTimeTile", "Agents (actief)", Ui.Duration(snapshot.Attention.AgentTime), $"{sessions} sessies · naast jouw tijd"),
            ("ScheduleTile", "Schema", schedule == 0 ? "—" : _WholeDuration(schedule), schedule == 0 ? "geen schema ingesteld" : _ScheduleDetail(snapshot.ScheduleMinutes)),
            ("CostTile", "Kosten", costText, costText == "geen prijs" ? $"{_Tokens(tokens)} tokens · geen tarief" : $"{cost.ToString("$0.##", CultureInfo.InvariantCulture)} gemeld"),
            ("OwnEntriesTile", "Eigen regels", snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture), $"{snapshot.Entries.Count(entry => entry.Status == Cockpit.Plugins.Abstractions.TimeTracking.CockpitTimeEntryStatus.Open)} open"),
        };
        for (var index = 0; index < values.Length; index++)
        {
            var card = _Tile(values[index].Item1, values[index].Item2, values[index].Item3, values[index].Item4);
            Grid.SetColumn(card, index);
            tiles.Children.Add(card);
        }

        return tiles;
    }

    private static Border _Tile(string name, string label, string value, string detail)
    {
        var card = new Border
        {
            Name = "OverviewTileCard",
            Height = 96,
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    Ui.Hint(label),
                    new TextBlock { Name = name, Text = value, FontSize = 22, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Name = $"{name}Detail", Text = detail, FontSize = 11, FontWeight = FontWeight.Normal, TextWrapping = TextWrapping.Wrap }
                        .Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush"),
                },
            },
        };
        card.Themed(BackgroundProperty, "CockpitPanelBgBrush");
        card.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush");
        return card;
    }

    private static string _ScheduleDetail(IReadOnlyList<int> minutes)
    {
        var labels = new[] { "ma", "di", "wo", "do", "vr", "za", "zo" };
        var parts = new List<string>();
        for (var start = 0; start < Math.Min(7, minutes.Count);)
        {
            var end = start;
            while (end + 1 < Math.Min(7, minutes.Count) && minutes[end + 1] == minutes[start])
            {
                end++;
            }

            if (minutes[start] > 0)
            {
                var days = start == end ? labels[start] : $"{labels[start]}–{labels[end]}";
                parts.Add($"{days} {_WholeDuration(minutes[start])}");
            }

            start = end + 1;
        }

        return string.Join(" · ", parts);
    }

    private static int _ScheduledMinutes(IReadOnlyList<int> minutes, DateOnly from, DateOnly to)
    {
        var total = 0;
        for (var day = from; day <= to; day = day.AddDays(1))
        {
            var index = ((int)day.DayOfWeek + 6) % 7;
            if (index < minutes.Count)
            {
                total += minutes[index];
            }
        }

        return total;
    }

    private static string _WholeDuration(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}u" : Ui.Duration(TimeSpan.FromMinutes(minutes));

    private static Control _Charts(ProductivityOverviewSnapshot snapshot, bool includeAgent)
    {
        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("11*,9*"), ColumnSpacing = 12 };
        columns.Children.Add(_DailyChart(snapshot));
        var right = new StackPanel { Spacing = 12, Children = { _Projects(snapshot, includeAgent), _Sessions(snapshot) } };
        Grid.SetColumn(right, 1);
        columns.Children.Add(right);
        return columns;
    }

    private static Control _DailyChart(ProductivityOverviewSnapshot snapshot)
    {
        var chart = new OverviewDailyChartControl
        {
            Name = "DailyOverviewChart",
            Height = 280,
            Items =
            [
                .. snapshot.Days.Select(day =>
                {
                    return new OverviewDailyDatum(
                        day.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch),
                        (day.Attention.Measured - day.Attention.Overlap + day.HistoricalMeasured).TotalHours,
                        day.Attention.Own.TotalHours,
                        day.Attention.AgentTime.TotalHours);
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
                _DailyLegend(),
                Ui.Hint("Per dag twee staven: links jouw tijd, rechts de agents. Agenttijd telt nooit op bij jouw tijd."),
            },
        }, 14);
    }

    private static Control _DailyLegend() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                _LegendItem(Lavender, "jouw tijd · gemeten"),
                _LegendItem(Own, "jouw tijd · eigen regels"),
                _LegendItem(Agent, "agents actief (naast jou, niet erbij)"),
            },
        };

    private static Control _ProjectLegend() =>
        new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Children =
            {
                _LegendItem(Lavender, "gemeten"),
                _LegendItem(Own, "eigen regels"),
                _LegendItem(Agent, "agent zonder jou"),
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

    private static Control _Projects(ProductivityOverviewSnapshot snapshot, bool includeAgent)
    {
        var projects = _ProjectRows(snapshot, includeAgent);
        var maximum = projects.Count == 0
            ? 1
            : projects.Max(project => (project.Measured + project.Own + (includeAgent ? project.Agent : TimeSpan.Zero)).TotalMinutes);
        var body = new StackPanel
        {
            Spacing = 7,
            Children =
            {
                new TextBlock { Text = "Tijd per project", FontSize = 15, FontWeight = FontWeight.SemiBold },
                _ProjectLegend(),
            },
        };
        foreach (var project in projects)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*,52"), ColumnSpacing = 8 };
            row.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = new SolidColorBrush(project.Color), VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = project.Label, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
            var segments = new List<ChartSegment>
            {
                new("gemeten", project.Measured.TotalMinutes, Lavender),
                new("eigen regels", project.Own.TotalMinutes, Own, true),
            };
            if (includeAgent)
            {
                segments.Add(new ChartSegment("agent zonder jou", project.Agent.TotalMinutes, Agent));
            }

            var totalTime = project.Measured + project.Own + (includeAgent ? project.Agent : TimeSpan.Zero);
            var chart = new HorizontalBarChartControl
            {
                Height = 30,
                MinWidth = 25,
                Segments = segments,
                TooltipText = project.Label,
            };
            var scale = new Grid { Name = "ProjectBarScale" };
            scale.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(1, totalTime.TotalMinutes), GridUnitType.Star)));
            scale.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(Math.Max(0, maximum - totalTime.TotalMinutes), GridUnitType.Star)));
            scale.Children.Add(chart);
            Grid.SetColumn(scale, 1);
            row.Children.Add(scale);
            var total = new TextBlock
            {
                Text = Ui.Duration(totalTime),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontWeight = FontWeight.SemiBold,
            };
            Grid.SetColumn(total, 2);
            row.Children.Add(total);
            body.Children.Add(row);
        }

        return Ui.Card(body, 14);
    }

    private static IReadOnlyList<_ProjectRow> _ProjectRows(ProductivityOverviewSnapshot snapshot, bool includeAgent)
    {
        const string none = "";
        var names = snapshot.Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        var measured = snapshot.Attention.MeasuredPerProject
            .Concat(snapshot.HistoricalMeasuredPerProject)
            .GroupBy(project => project.ProjectId ?? none, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TimeSpan.FromTicks(group.Sum(project => project.Time.Ticks)), StringComparer.Ordinal);
        var own = snapshot.Entries
            .GroupBy(entry => entry.ProjectId ?? none, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TimeSpan.FromTicks(group.Sum(entry => entry.Duration.Ticks)), StringComparer.Ordinal);
        var agent = includeAgent
            ? snapshot.AgentWithoutYouPerProject.ToDictionary(project => project.ProjectId ?? none, project => project.Time, StringComparer.Ordinal)
            : new Dictionary<string, TimeSpan>(StringComparer.Ordinal);

        return measured.Keys.Concat(own.Keys).Concat(agent.Keys).Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                var brush = MeasuredTimeCard.ProjectBrush(id);
                var color = brush is ISolidColorBrush solid ? solid.Color : Lavender;
                return new _ProjectRow(
                    id.Length == 0 ? "geen project" : names.GetValueOrDefault(id, id),
                    measured.GetValueOrDefault(id),
                    own.GetValueOrDefault(id),
                    agent.GetValueOrDefault(id),
                    color);
            })
            .OrderByDescending(row => row.Measured + row.Own + row.Agent)
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
                _TableHead(["Sessie", "Profiel", "Actief", "Zonder jou", "Tokens", "Kosten"], "120,104,58,68,58,66"),
            },
        };
        var active = snapshot.Attention.Agents
            .GroupBy(span => span.Owner ?? string.Empty, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => TimeSpan.FromTicks(group.Sum(span => span.Duration.Ticks)), StringComparer.Ordinal);
        foreach (var day in snapshot.Timeline.Days)
        {
            active[day.PaneId] = active.GetValueOrDefault(day.PaneId) + day.AgentTime;
        }

        var rawUsage = snapshot.Timeline.Usage.GroupBy(value => value.PaneId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var historical = snapshot.Timeline.Days.GroupBy(value => value.PaneId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var sessions = rawUsage.Keys.Concat(historical.Keys).Concat(active.Keys).Distinct(StringComparer.Ordinal).ToList();
        foreach (var session in sessions)
        {
            var usage = rawUsage.GetValueOrDefault(session) ?? [];
            var days = historical.GetValueOrDefault(session) ?? [];
            var tokens = usage.Sum(value => (long)value.InputTokens + value.OutputTokens + value.CacheReadInputTokens + value.CacheCreationInputTokens)
                + days.Sum(value => (long)value.InputTokens + value.OutputTokens + value.CacheReadInputTokens + value.CacheCreationInputTokens);
            var cost = usage.Sum(value => value.CostUsd) + days.Sum(value => value.ReportedCostUsd);
            var hasUnpriced = usage.Any(value => _TokenCount(value) > 0 && value.CostUsd == 0)
                || days.Any(value => _TokenCount(value) > 0 && value.ReportedCostUsd == 0);
            var model = usage.Select(value => value.Model)
                .Concat(days.Select(value => value.Model))
                .LastOrDefault(value => string.IsNullOrWhiteSpace(value) is false)
                ?? "onbekend";
            var withoutYou = snapshot.Attention.AgentWithoutYouPerSession.GetValueOrDefault(session)
                + TimeSpan.FromTicks(days.Sum(value => value.AgentTime.Ticks));
            body.Children.Add(_TableRow(
                [
                    session,
                    model,
                    Ui.Duration(active.GetValueOrDefault(session)),
                    Ui.Duration(withoutYou),
                    _Tokens(tokens),
                    hasUnpriced ? "geen prijs" : cost.ToString("$0.00", CultureInfo.InvariantCulture),
                ],
                "120,104,58,68,58,66"));
        }

        if (sessions.Count == 0)
        {
            body.Children.Add(Ui.Hint("Geen agents in dit bereik."));
        }

        return Ui.Card(body, 14);
    }

    private static string _Tokens(long tokens) =>
        tokens >= 1000 ? $"{tokens / 1000d:0.#}k" : tokens.ToString(CultureInfo.InvariantCulture);

    private static long _TokenCount(Cockpit.Plugins.Abstractions.TimeTracking.CockpitUsageDelta usage) =>
        (long)usage.InputTokens + usage.OutputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;

    private static long _TokenCount(Cockpit.Plugins.Abstractions.TimeTracking.CockpitTimelineDay day) =>
        (long)day.InputTokens + day.OutputTokens + day.CacheReadInputTokens + day.CacheCreationInputTokens;

    private Control _Entries(ProductivityOverviewSnapshot snapshot)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = "Eigen regels deze week", FontSize = 15, FontWeight = FontWeight.SemiBold });
        var add = new Button { Name = "AddOverviewEntry", Content = "+ Regel", Classes = { "Compact" } };
        add.Click += (_, _) => _openHours();
        Grid.SetColumn(add, 1);
        heading.Children.Add(add);
        var body = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                heading,
                _TableHead(["Dag", "Omschrijving", "Project", "Duur", "Status"], "80,*,160,70,90"),
            },
        };
        var projects = snapshot.Projects.ToDictionary(project => project.Id, project => project.Name, StringComparer.Ordinal);
        foreach (var entry in snapshot.Entries.OrderBy(entry => entry.StartedAt))
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("80,*,160,70,90"), ColumnSpacing = 8 };
            _AddCell(row, 0, entry.StartedAt.ToLocalTime().ToString("ddd d", Dutch));
            _AddCell(row, 1, entry.Description);
            var projectId = entry.ProjectId ?? string.Empty;
            var project = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Avalonia.Controls.Shapes.Ellipse
                    {
                        Name = "OverviewEntryProjectDot",
                        Width = 8,
                        Height = 8,
                        Fill = MeasuredTimeCard.ProjectBrush(projectId),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock { Text = projectId.Length == 0 ? "geen project" : projects.GetValueOrDefault(projectId, projectId), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            };
            Grid.SetColumn(project, 2);
            row.Children.Add(project);
            _AddCell(row, 3, _ClockDuration(entry.Duration));
            _AddCell(row, 4, Ui.Status(entry));
            body.Children.Add(_TableBorder(row));
        }

        if (snapshot.Entries.Count == 0)
        {
            body.Children.Add(Ui.Hint("Geen eigen regels in dit bereik."));
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

        return _TableBorder(row);
    }

    private static void _AddCell(Grid row, int column, string text)
    {
        var value = new TextBlock { Text = text, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
        Grid.SetColumn(value, column);
        row.Children.Add(value);
    }

    private static Border _TableBorder(Control child) =>
        new Border
        {
            Child = child,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 5),
        }.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");

    private static string _ClockDuration(TimeSpan duration) =>
        $"{(int)duration.TotalHours}:{duration.Minutes:00}";

    private sealed record OverviewDailyDatum(string Label, double Measured, double Own, double Agent);

    private sealed class OverviewDailyChartControl : ChartControl
    {
        public IReadOnlyList<OverviewDailyDatum> Items { get; init; } = [];

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (Items.Count == 0 || Bounds.Width < 100 || Bounds.Height < 100)
            {
                Empty(context, Bounds);
                return;
            }

            const double left = 38;
            const double top = 24;
            const double bottom = 28;
            var plotHeight = Bounds.Height - top - bottom;
            var maximum = Math.Max(10, Math.Ceiling(Items.Max(item => Math.Max(item.Measured + item.Own, item.Agent))));
            var muted = Brush("ThemeForegroundLowBrush", Brushes.Gray);
            var foreground = Brush("ThemeForegroundBrush", Brushes.Black);
            var gridPen = new Pen(Brush("CockpitHairlineSoftBrush", Brushes.DimGray), 1);
            for (var step = 0; step <= 4; step++)
            {
                var value = maximum * step / 4;
                var y = top + plotHeight - (plotHeight * step / 4);
                context.DrawLine(gridPen, new Point(left, y), new Point(Bounds.Width - 8, y));
                var scale = new FormattedText($"{value:0.#}u", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, muted);
                context.DrawText(scale, new Point(2, y - scale.Height / 2));
            }

            var group = (Bounds.Width - left - 8) / Items.Count;
            for (var index = 0; index < Items.Count; index++)
            {
                var item = Items[index];
                var barWidth = Math.Max(5, Math.Min(22, (group - 10) / 2));
                var measuredHeight = Math.Max(0, item.Measured) / maximum * plotHeight;
                var ownHeight = Math.Max(0, item.Own) / maximum * plotHeight;
                var agentHeight = Math.Max(0, item.Agent) / maximum * plotHeight;
                var x = left + index * group + Math.Max(2, (group - (barWidth * 2 + 4)) / 2);
                var baseline = top + plotHeight;
                var measured = new Rect(x, baseline - measuredHeight, barWidth, measuredHeight);
                context.DrawRectangle(new SolidColorBrush(Lavender), null, measured);
                var own = new Rect(x, baseline - measuredHeight - ownHeight, barWidth, ownHeight);
                context.DrawRectangle(new SolidColorBrush(Own), null, own);
                Hatch(context, own);
                var agent = new Rect(x + barWidth + 4, baseline - agentHeight, barWidth, agentHeight);
                context.DrawRectangle(new SolidColorBrush(Agent), null, agent);
                _DrawValue(context, item.Measured + item.Own, x, baseline - measuredHeight - ownHeight, foreground);
                _DrawValue(context, item.Agent, x + barWidth + 4, baseline - agentHeight, foreground);
                var label = new FormattedText(item.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, muted);
                context.DrawText(label, new Point(x, Bounds.Height - label.Height));
            }
        }

        private static void _DrawValue(DrawingContext context, double hours, double x, double y, IBrush brush)
        {
            if (hours <= 0)
            {
                return;
            }

            var duration = TimeSpan.FromHours(hours);
            var text = new FormattedText(_ClockDuration(duration), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, brush);
            context.DrawText(text, new Point(x, Math.Max(0, y - text.Height - 2)));
        }
    }

    private sealed record _ProjectRow(string Label, TimeSpan Measured, TimeSpan Own, TimeSpan Agent, Color Color);
}

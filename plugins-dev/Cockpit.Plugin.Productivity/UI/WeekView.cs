using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.Productivity.UI;

// Uren · Week (AC-1723): the schedule, what goes along and the proposal per day, as mockup v3 03 and 04 draw them.
internal static class WeekView
{
    private const string Waiting = "CockpitStatusWaitingBrush";
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private static readonly IBrush Ok = new ImmutableSolidColorBrush(Color.Parse("#7CC596"));
    private static readonly IBrush OwnRow = new ImmutableSolidColorBrush(Color.Parse("#08E0A33E"));

    public static Control Build(
        WeekProposal week,
        WeekSettings settings,
        IReadOnlyList<PluginProjectInfo> projects,
        bool thisWeek,
        int weekNumber,
        Action<DateOnly> openDay,
        Action copy)
    {
        var body = new StackPanel { Spacing = 12 };
        if (thisWeek)
        {
            var cards = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12 };
            cards.Children.Add(_ScheduleCard(settings));
            var included = _IncludedCard(settings, projects.Count > 0
                ? projects
                : [.. week.Lines.Where(line => line.Own == false && line.ProjectId is not null).Select(line => new PluginProjectInfo(line.ProjectId ?? string.Empty, line.Label))]);
            Grid.SetColumn(included, 1);
            cards.Children.Add(included);
            body.Children.Add(cards);
        }
        else
        {
            body.Children.Add(Ui.Card(_Rich(
                new Run("🕘  Je kijkt naar "),
                new Run($"week {weekNumber}") { FontWeight = FontWeight.SemiBold },
                new Run(". Dezelfde weergave als nu, met het voorstel en de status van toen. Ruwe tijdlijn tot 90 dagen terug, daarna dagtotalen per project; eigen regels en terugmeldingen blijven altijd bewaard.")), 14));
        }

        body.Children.Add(_ProposalCard(week, settings, thisWeek, weekNumber, openDay, copy));
        return body;
    }

    private static Control _ScheduleCard(WeekSettings settings)
    {
        var heading = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Children = { _Title("Werkschema"), Ui.Pill("harde grens", Waiting, "CockpitStatusWaitingChipTintBrush") },
        };
        var days = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*"), ColumnSpacing = 8 };
        string[] names = ["ma", "di", "wo", "do", "vr", "za", "zo"];
        for (var index = 0; index < names.Length; index++)
        {
            var hours = settings.ScheduleMinutes[index] / 60.0;
            var day = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    Ui.Hint(names[index]),
                    new Border
                    {
                        Padding = new Thickness(8, 5),
                        CornerRadius = new CornerRadius(8),
                        BorderThickness = new Thickness(1),
                        Child = new TextBlock { Text = hours.ToString("0.##", Dutch) },
                    }.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush"),
                },
            };
            Grid.SetColumn(day, index);
            days.Children.Add(day);
        }

        var rounding = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var minutes in new[] { 5, 15, 30 })
        {
            var option = new Border { Padding = new Thickness(10, 4), CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = $"{minutes}m" } };
            if (minutes == settings.RoundingMinutes)
            {
                option.Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");
                option.BorderThickness = new Thickness(1);
                option.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush");
            }

            rounding.Children.Add(option);
        }

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), ColumnSpacing = 10 };
        var roundingLabel = new TextBlock { Text = "Afronden op", VerticalAlignment = VerticalAlignment.Center };
        footer.Children.Add(roundingLabel);
        Grid.SetColumn(rounding, 1);
        footer.Children.Add(rounding);
        var total = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Hint("Totaal"),
                new TextBlock { Text = $"{(settings.ScheduleMinutes.Sum() / 60.0).ToString("0.##", Dutch)}u", FontWeight = FontWeight.Bold },
            },
        };
        Grid.SetColumn(total, 3);
        footer.Children.Add(total);

        return Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                heading,
                days,
                footer,
                Ui.Hint("De agent boekt per dag nooit meer dan het schema, met wat er al geboekt staat meegerekend. Komt een dag erboven, dan boekt hij die dag niets en legt hij hem eerst voor. Aanpassen doe je in de instellingen van Productivity."),
            },
        }, 14);
    }

    private static Control _IncludedCard(WeekSettings settings, IReadOnlyList<PluginProjectInfo> projects)
    {
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var project in projects)
        {
            var isPrivate = settings.PrivateProjects.Contains(project.Id, StringComparer.Ordinal);
            var chip = isPrivate
                ? Ui.Pill($"{project.Name} · privé", "CockpitTextSecondaryBrush", "CockpitHairlineSoftBrush")
                : Ui.Pill(project.Name, "CockpitStatusDoneBrush", "CockpitStatusDoneChipTintBrush");
            chip.Margin = new Thickness(0, 0, 6, 4);
            chips.Children.Add(chip);
        }

        var rows = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 14, RowSpacing = 8 };
        _Cell(rows, Ui.Hint("Projecten"), 0, 0);
        _Cell(rows, chips, 0, 1);
        _Cell(rows, Ui.Hint("Eigen regels"), 1, 0);
        _Cell(rows, new TextBlock { Text = "alle open regels van de week, ook zonder project", FontSize = 12.5, TextWrapping = TextWrapping.Wrap }, 1, 1);
        _Cell(rows, Ui.Hint("Instructies"), 2, 0);
        _Cell(rows, Ui.Hint("komen met de overdracht aan een agent"), 2, 1);

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(_Title("Wat er meegaat"));
        var instructions = new Button { Content = "Instructies bewerken", IsEnabled = false, Classes = { "Compact" } };
        ToolTip.SetTip(instructions, "komt");
        Grid.SetColumn(instructions, 1);
        heading.Children.Add(instructions);

        return Ui.Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                heading,
                rows,
                Ui.Hint("Privé-projecten tellen wel als jouw tijd in het overzicht, maar komen niet in het voorstel. Geen koppeltabel naar een boekhoudpakket: dat zoekt de agent zelf uit."),
            },
        }, 14);
    }

    private static Control _ProposalCard(WeekProposal week, WeekSettings settings, bool thisWeek, int weekNumber, Action<DateOnly> openDay, Action copy)
    {
        var days = week.VisibleDays;
        var columns = "*," + string.Join(',', days.Select(_ => "62")) + ",62,250";
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(_Title(thisWeek ? "Voorstel per dag" : $"Voorstel per dag · week {weekNumber}"));
        var legend = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _OwnTag(), Ui.Hint("= jouw eigen regel, telt als bezet binnen het schema · klik een dag om naar Dag te gaan") },
        };
        Grid.SetColumn(legend, 1);
        heading.Children.Add(legend);

        var table = new StackPanel();
        var header = _Row(columns);
        _Cell(header, Ui.Hint("Regel"), 0, 0);
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index].Day;
            var open = new Button
            {
                Name = "OpenDay",
                Content = day.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Right,
                FontSize = 11.5,
            };
            open.Themed(Button.ForegroundProperty, "CockpitTextSecondaryBrush");
            open.Click += (_, _) => openDay(day);
            _Cell(header, open, 0, index + 1);
        }

        _Cell(header, _Right(Ui.Hint("Week")), 0, days.Count + 1);
        _Cell(header, Ui.Hint("Status"), 0, days.Count + 2);
        table.Children.Add(_Line(header));

        var schedule = _Row(columns);
        _Cell(schedule, new TextBlock { Text = "Schema (grens)" }, 0, 0);
        _Values(schedule, [.. days.Select(day => WeekProposal.Text(day.Schedule)), WeekProposal.Text(TimeSpan.FromTicks(days.Sum(day => day.Schedule.Ticks)))], null);
        table.Children.Add(_Line(schedule));

        foreach (var line in week.Lines)
        {
            var row = _Row(columns);
            row.Name = "ProposalRow";
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (line.Own)
            {
                label.Children.Add(_OwnTag());
            }
            else
            {
                label.Children.Add(new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8, Fill = MeasuredTimeCard.ProjectBrush(line.ProjectId), VerticalAlignment = VerticalAlignment.Center });
            }

            label.Children.Add(new TextBlock { Text = line.Label, VerticalAlignment = VerticalAlignment.Center });
            if (line.Detail.Length > 0)
            {
                var detail = Ui.Hint($"· {line.Detail}");
                detail.VerticalAlignment = VerticalAlignment.Center;
                label.Children.Add(detail);
            }

            _Cell(row, label, 0, 0);
            _Values(row, [.. days.Select(day => WeekProposal.Cell(line, day.Day)), WeekProposal.Text(TimeSpan.FromTicks(line.Cells.Sum(cell => cell.Time.Ticks)))], line.Own ? Waiting : null);
            _Cell(row, _Status(line), 0, days.Count + 2);
            var border = _Line(row);
            if (line.Own)
            {
                border.Background = OwnRow;
            }

            table.Children.Add(border);
        }

        var totals = _Row(columns);
        _Cell(totals, new TextBlock { Text = "Totaal voorstel", FontWeight = FontWeight.Bold }, 0, 0);
        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var text = new TextBlock { Text = WeekProposal.Text(day.Total) + (day.OverSchedule ? " ⚠" : string.Empty), FontWeight = FontWeight.Bold };
            if (day.OverSchedule)
            {
                text.Themed(TextBlock.ForegroundProperty, Waiting);
            }
            else
            {
                text.Foreground = Ok;
            }

            _Cell(totals, _Right(text), 0, index + 1);
        }

        _Cell(totals, _Right(new TextBlock { Text = WeekProposal.Text(TimeSpan.FromTicks(days.Sum(day => day.Total.Ticks))), FontWeight = FontWeight.Bold, Foreground = Ok }), 0, days.Count + 1);
        table.Children.Add(new Border { Child = totals, BorderThickness = new Thickness(0, 1, 0, 1) }.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush"));

        var measured = _Row(columns);
        _Cell(measured, Ui.Hint("gemeten (jouw tijd, zonder eigen regels)"), 0, 0);
        _Values(measured, [.. days.Select(day => WeekProposal.Text(day.Measured)), WeekProposal.Text(TimeSpan.FromTicks(days.Sum(day => day.Measured.Ticks)))], "CockpitTextSecondaryBrush");
        table.Children.Add(measured);

        var body = new StackPanel { Spacing = 10, Children = { heading, table } };
        foreach (var day in days.Where(day => day.OverSchedule))
        {
            body.Children.Add(_Warning(day));
        }

        if (thisWeek && days.All(day => day.OverSchedule == false))
        {
            body.Children.Add(_Explanation(settings));
        }

        body.Children.Add(_Footer(week, thisWeek, copy));
        return Ui.Card(body, 14);
    }

    private static Control _Status(ProposalLine line)
    {
        var text = WeekProposal.Status(line);
        if (text.StartsWith("geboekt", StringComparison.Ordinal))
        {
            var booked = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { Ui.Pill("✓ geboekt", "CockpitStatusDoneBrush", "CockpitStatusDoneChipTintBrush") } };
            if (text.Length > "geboekt".Length)
            {
                booked.Children.Add(Ui.Hint(text["geboekt".Length..].Trim()));
            }

            return booked;
        }

        var status = new TextBlock { Text = text.StartsWith("niet geboekt", StringComparison.Ordinal) ? "⚠ " + text : text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        return status.Themed(TextBlock.ForegroundProperty, text.StartsWith("niet geboekt", StringComparison.Ordinal) ? Waiting : "CockpitTextSecondaryBrush");
    }

    private static Control _Warning(ProposalDay day) =>
        new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = _Rich(
                new Run("⚠  "),
                new Run($"{day.Day.ToDateTime(TimeOnly.MinValue).ToString("ddd d", Dutch)} staat nu op {WeekProposal.Text(day.Total)} tegenover {WeekProposal.Text(day.Schedule)}") { FontWeight = FontWeight.Bold },
                new Run(": de eigen en geboekte regels komen samen boven het schema. Bied je hem aan, dan legt de agent die dag voor in plaats van te boeken. Pas hem aan of verplaats hem.")),
        }.Themed(Border.BorderBrushProperty, Waiting)
            .Themed(Border.BackgroundProperty, "CockpitStatusWaitingChipTintBrush");

    private static Control _Explanation(WeekSettings settings) =>
        new Border
        {
            Padding = new Thickness(14, 10),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = new ImmutableSolidColorBrush(Color.Parse("#242563EB")),
            BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#552563EB")),
            Child = _Rich(
                new Run("Zo komt het voorstel tot stand: per dag "),
                new Run("schema − eigen regels") { FontWeight = FontWeight.Bold },
                new Run(" = te verdelen. Dat verdeel je over de projecten in het voorstel naar verhouding van de "),
                new Run("gemeten tijd") { FontWeight = FontWeight.Bold },
                new Run($", afgerond op {settings.RoundingMinutes}m; het afrondverschil gaat naar het grootste project. Wat er al in de boekhouding staat, rekent de agent zelf mee.")),
        };

    private static Control _Footer(WeekProposal week, bool thisWeek, Action copy)
    {
        var copyButton = new Button { Name = "CopyTable", Content = "Kopieer als tabel" };
        copyButton.Click += (_, _) => copy();
        var openCount = week.OpenLines.Count;
        var agent = new Button
        {
            Name = "GiveToAgent",
            Content = thisWeek ? "Geef aan een agent…" : $"Geef {openCount} open regels aan een agent…",
            IsEnabled = false,
            Classes = { "Accent" },
        };
        ToolTip.SetTip(agent, "komt");
        var hint = Ui.Hint(thisWeek
            ? "Per regel één omschrijving: de tickets van die dag. Je kunt elk vak aanpassen."
            : "Alleen open regels gaan mee. Geboekte regels zijn alleen-lezen en gaan nooit opnieuw mee.");
        hint.VerticalAlignment = VerticalAlignment.Center;
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(hint);
        Grid.SetColumn(copyButton, 1);
        footer.Children.Add(copyButton);
        Grid.SetColumn(agent, 2);
        footer.Children.Add(agent);
        var soon = Ui.Tag("komt");
        soon.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(soon, 3);
        footer.Children.Add(soon);
        return footer;
    }

    private static void _Values(Grid row, IReadOnlyList<string> values, string? brush)
    {
        for (var index = 0; index < values.Count; index++)
        {
            var text = new TextBlock { Text = values[index] };
            if (values[index] == "—")
            {
                text.Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush");
            }
            else if (brush is not null)
            {
                text.Themed(TextBlock.ForegroundProperty, brush);
            }

            _Cell(row, _Right(text), 0, index + 1);
        }
    }

    private static Grid _Row(string columns) =>
        new() { ColumnDefinitions = new ColumnDefinitions(columns), ColumnSpacing = 10, Margin = new Thickness(6, 6) };

    private static Border _Line(Control row) =>
        new Border { Child = row, BorderThickness = new Thickness(0, 0, 0, 1) }.Themed(Border.BorderBrushProperty, "CockpitHairlineSoftBrush");

    private static void _Cell(Grid grid, Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static T _Right<T>(T control) where T : Control
    {
        control.HorizontalAlignment = HorizontalAlignment.Right;
        control.VerticalAlignment = VerticalAlignment.Center;
        return control;
    }

    private static Border _OwnTag() =>
        new Border
        {
            Padding = new Thickness(6, 0),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#55E0A33E")),
            Child = new TextBlock { Text = "eigen", FontSize = 11 }.Themed(TextBlock.ForegroundProperty, Waiting),
        }.Themed(Border.BackgroundProperty, "CockpitStatusWaitingChipTintBrush");

    private static TextBlock _Rich(params Run[] runs)
    {
        var inlines = new InlineCollection();
        inlines.AddRange(runs);
        return new TextBlock { TextWrapping = TextWrapping.Wrap, Inlines = inlines };
    }

    private static TextBlock _Title(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
}

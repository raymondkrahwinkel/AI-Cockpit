using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

// The week proposal's settings (AC-1723): the schedule per day, the rounding and which projects stay private; and
// the price per profile for usage its provider reports no cost for (AC-1766).
internal sealed class ProductivitySettingsControl : UserControl, IPluginSettingsView
{
    private static readonly CultureInfo Dutch = CultureInfo.GetCultureInfo("nl-NL");
    private static readonly string[] DayNames = ["ma", "di", "wo", "do", "vr", "za", "zo"];
    private readonly ICockpitUiHost _host;
    private readonly ProductivityController _controller;
    private readonly WeekSettings _settings;
    private readonly TextBox[] _hours;
    private readonly ComboBox _rounding = new() { ItemsSource = new[] { "5 minuten", "15 minuten", "30 minuten" }, Width = 140 };
    private readonly TextBox _instructions;
    private readonly StackPanel _projects = new() { Spacing = 4 };
    private readonly List<(string Id, CheckBox Box)> _private = [];
    private readonly StackPanel _rateRows = new() { Spacing = 4 };
    private readonly List<(string Profile, TextBox Box)> _rates = [];
    private readonly Dictionary<string, double> _storedRates;

    public ProductivitySettingsControl(ICockpitUiHost host, ProductivityController controller)
    {
        _host = host;
        _controller = controller;
        _settings = host.Storage.Get<WeekSettings>(WeekSettings.StorageKey) ?? WeekSettings.Default;
        _storedRates = host.Storage.Get<Dictionary<string, double>>(ProfileRates.StorageKey) ?? [];
        _instructions = new TextBox
        {
            Name = "AgentInstructions",
            Text = _settings.AgentInstructions,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 110,
        };
        _hours = [.. _settings.ScheduleMinutes.Select((minutes, index) => new TextBox { Name = $"Schedule{index}", Text = (minutes / 60.0).ToString("0.##", Dutch), Width = 56 })];
        _rounding.SelectedIndex = Array.IndexOf([5, 15, 30], _settings.RoundingMinutes) is var index and >= 0 ? index : 1;

        var schedule = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        for (var day = 0; day < DayNames.Length; day++)
        {
            schedule.Children.Add(new StackPanel { Spacing = 3, Children = { new TextBlock { Text = DayNames[day], Opacity = 0.7 }, _hours[day] } });
        }

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Werkschema in uren per dag · harde grens", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                schedule,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Afronden op", VerticalAlignment = VerticalAlignment.Center }, _rounding } },
                new TextBlock { Text = "Projecten in het voorstel", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "Een privé-project telt wel als jouw tijd, maar komt niet in het voorstel.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _projects,
                new TextBlock { Text = "Instructies voor de boekingsagent", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "Deze tekst is bewerkbaar. De harde schemagrens en het verplichte akkoord staan vast buiten deze tekst.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _instructions,
                new TextBlock { Text = "Tarief per profiel", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "In dollar per miljoen tokens (invoer, uitvoer en cache-schrijven), voor een profiel waarvan de aanbieder geen kosten meldt. Leeg laat het zonder prijs.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _rateRows,
            },
        };
        _ = _LoadProjectsAsync();
        _ = _LoadProfilesAsync();
    }

    public bool TryStage(out Action? commit, out string? error)
    {
        var minutes = new int[_hours.Length];
        for (var day = 0; day < _hours.Length; day++)
        {
            if (double.TryParse(_hours[day].Text?.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var hours) == false || hours > 24)
            {
                commit = null;
                error = $"Vul voor {DayNames[day]} een aantal uren in tussen 0 en 24.";
                return false;
            }

            minutes[day] = (int)Math.Round(hours * 60);
        }

        var rates = new Dictionary<string, double>(_storedRates, StringComparer.Ordinal);
        foreach (var (profile, box) in _rates)
        {
            if (string.IsNullOrWhiteSpace(box.Text))
            {
                rates.Remove(profile);
            }
            else if (double.TryParse(box.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate))
            {
                rates[profile] = rate;
            }
            else
            {
                commit = null;
                error = $"Vul voor {profile} een tarief in, bijvoorbeeld 3,5, of laat het leeg.";
                return false;
            }
        }

        var settings = new WeekSettings(
            minutes,
            new[] { 5, 15, 30 }[Math.Max(_rounding.SelectedIndex, 0)],
            [.. _settings.PrivateProjects.Where(id => _private.All(project => project.Id != id)), .. _private.Where(project => project.Box.IsChecked == true).Select(project => project.Id)])
        {
            AgentInstructions = _instructions.Text ?? string.Empty,
        };
        commit = () =>
        {
            _host.Storage.Set(WeekSettings.StorageKey, settings);
            _host.Storage.Set(ProfileRates.StorageKey, rates);
            _ = _controller.RefreshAsync();
        };
        error = null;
        return true;
    }

    private async Task _LoadProfilesAsync()
    {
        try
        {
            foreach (var profile in await _host.GetProfilesAsync() ?? [])
            {
                var box = new TextBox
                {
                    Name = $"Rate{_rates.Count}",
                    Width = 80,
                    Text = _storedRates.TryGetValue(profile.Label, out var rate) ? rate.ToString("0.##", Dutch) : string.Empty,
                };
                _rates.Add((profile.Label, box));
                _rateRows.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new TextBlock { Text = profile.Label, Width = 140, VerticalAlignment = VerticalAlignment.Center }, box },
                });
            }
        }
        catch (Exception exception)
        {
            _host.ShowToast($"De profielen konden niet worden geladen: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _LoadProjectsAsync()
    {
        try
        {
            foreach (var project in await _host.GetProjectsAsync() ?? [])
            {
                var box = new CheckBox { Content = $"{project.Name} · privé", IsChecked = _settings.PrivateProjects.Contains(project.Id, StringComparer.Ordinal) };
                _private.Add((project.Id, box));
                _projects.Children.Add(box);
            }
        }
        catch (Exception exception)
        {
            _host.ShowToast($"De projecten konden niet worden geladen: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}

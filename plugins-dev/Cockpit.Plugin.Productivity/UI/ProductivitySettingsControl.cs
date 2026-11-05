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
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
    private static readonly string[] DayNames = ["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"];
    private readonly ICockpitUiHost _host;
    private readonly ProductivityController _controller;
    private readonly WeekSettings _settings;
    private readonly TextBox[] _hours;
    private readonly ComboBox _rounding = new() { ItemsSource = new[] { "5 minutes", "15 minutes", "30 minutes" }, Width = 140 };
    private readonly TextBox _refreshSeconds;
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
        _refreshSeconds = new TextBox { Name = "RefreshSeconds", Text = _settings.RefreshSeconds.ToString(English), Width = 70 };
        _instructions = new TextBox
        {
            Name = "AgentInstructions",
            Text = _settings.AgentInstructions,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MinHeight = 110,
        };
        _hours = [.. _settings.ScheduleMinutes.Select((minutes, index) => new TextBox { Name = $"Schedule{index}", Text = (minutes / 60.0).ToString("0.##", English), Width = 56 })];
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
                new TextBlock { Text = "Schedule in hours per day · hard limit", FontWeight = Avalonia.Media.FontWeight.SemiBold },
                schedule,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Round to", VerticalAlignment = VerticalAlignment.Center }, _rounding } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Hours and Overview refresh every (seconds)", VerticalAlignment = VerticalAlignment.Center }, _refreshSeconds } },
                new TextBlock { Text = "Projects in the proposal", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "A private project still counts as your time, but it does not go into the proposal.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _projects,
                new TextBlock { Text = "Instructions for the booking agent", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "This text is editable. The hard schedule limit and the required approval are fixed outside this text.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                _instructions,
                new TextBlock { Text = "Rate per profile", FontWeight = Avalonia.Media.FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                new TextBlock { Text = "In dollars per million tokens (input, output and cache writes), for a profile whose provider reports no cost. Empty leaves it unpriced.", Opacity = 0.7, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
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
                error = $"Enter a number of hours between 0 and 24 for {DayNames[day]}.";
                return false;
            }

            minutes[day] = (int)Math.Round(hours * 60);
        }

        if (int.TryParse(_refreshSeconds.Text, NumberStyles.None, English, out var refreshSeconds) == false || refreshSeconds is < 15 or > 3600)
        {
            commit = null;
            error = "Enter a number of seconds between 15 and 3600 for the refresh.";
            return false;
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
                error = $"Enter a rate for {profile}, for example 3.5, or leave it empty.";
                return false;
            }
        }

        var settings = new WeekSettings(
            minutes,
            new[] { 5, 15, 30 }[Math.Max(_rounding.SelectedIndex, 0)],
            [.. _settings.PrivateProjects.Where(id => _private.All(project => project.Id != id)), .. _private.Where(project => project.Box.IsChecked == true).Select(project => project.Id)])
        {
            AgentInstructions = _instructions.Text ?? string.Empty,
            RefreshSeconds = refreshSeconds,
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
                    Text = _storedRates.TryGetValue(profile.Label, out var rate) ? rate.ToString("0.##", English) : string.Empty,
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
            _host.ShowToast($"The profiles could not be loaded: {exception.Message}", PluginToastSeverity.Error);
        }
    }

    private async Task _LoadProjectsAsync()
    {
        try
        {
            foreach (var project in await _host.GetProjectsAsync() ?? [])
            {
                var box = new CheckBox { Content = $"{project.Name} · private", IsChecked = _settings.PrivateProjects.Contains(project.Id, StringComparer.Ordinal) };
                _private.Add((project.Id, box));
                _projects.Children.Add(box);
            }
        }
        catch (Exception exception)
        {
            _host.ShowToast($"The projects could not be loaded: {exception.Message}", PluginToastSeverity.Error);
        }
    }
}

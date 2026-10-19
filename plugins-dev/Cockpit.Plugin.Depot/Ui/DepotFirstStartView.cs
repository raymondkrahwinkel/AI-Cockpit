using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI.Onboarding;

namespace Cockpit.Plugin.Depot.UI;

// AC-1509: route C on the first start. Sign in to a Depot (the browser opens only on the click, AC-1517), then restore the
// latest backup (C1) or set up from the profile and organisation (C2). Offline is a state with a way on, not an error;
// nothing here writes a connection, the backend part keeps one only after a sign-in that succeeded.
internal sealed class DepotFirstStartView : UserControl
{
    private readonly IFirstStartHost _host;
    private readonly TextBox _url = new() { Name = "DepotFirstStartUrl", PlaceholderText = "https://depot.example.com" };
    private readonly Button _signIn = new() { Name = "DepotFirstStartSignIn", Content = "Sign in", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Name = "DepotFirstStartStatus", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly StackPanel _choices = new() { Spacing = 8, IsVisible = false };
    private readonly Button _restore = new() { Name = "DepotFirstStartRestore", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly Button _setUp = new() { Name = "DepotFirstStartSetUp", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _consent = new() { Spacing = 4, IsVisible = false };
    private readonly TextBlock _outcome = new() { Name = "DepotFirstStartOutcome", FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private DepotFirstStartAnswer? _answer;
    private TaskCompletionSource<bool>? _agreed;

    public DepotFirstStartView(IFirstStartHost host)
    {
        _host = host;
        _signIn.Click += async (_, _) => await SignInAsync().ConfigureAwait(true);
        _restore.Click += async (_, _) => await _RestoreAsync().ConfigureAwait(true);
        _setUp.Click += async (_, _) => await _SetUpAsync().ConfigureAwait(true);

        _choices.Children.AddRange([_Hint("What next?"), _restore, _setUp, _consent, _outcome]);

        var signInRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { _signIn, _status } };
        Grid.SetColumn(_status, 1);

        Content = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(4, 0),
            Children =
            {
                _Hint("Your Depot's address"),
                _url,
                _Hint("Signing in opens Depot's sign-in page in your browser."),
                signInRow,
                _choices,
            },
        };
    }

    // Public, like the settings row's SignInAsync, so a journey can drive it and await it.
    public async Task SignInAsync()
    {
        _signIn.IsEnabled = false;
        _choices.IsVisible = false;
        _status.Text = "Signing in…";
        try
        {
            var payload = JsonSerializer.SerializeToElement(new DepotFirstStartSignInRequest(_url.Text ?? string.Empty), DepotChannel.Json);
            var answer = (await _host.Channel.InvokeAsync(DepotChannel.FirstStartSignIn, payload).ConfigureAwait(true)).Deserialize<DepotFirstStartAnswer>(DepotChannel.Json);
            _Show(answer ?? new DepotFirstStartAnswer(DepotFirstStartState.NotSignedIn, "Depot gave no answer. Try again."));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _Show(new DepotFirstStartAnswer(DepotFirstStartState.NotSignedIn, $"Signing in failed: {exception.Message}"));
        }
        finally
        {
            _signIn.IsEnabled = true;
        }
    }

    private void _Show(DepotFirstStartAnswer answer)
    {
        _answer = answer;
        _status.Text = answer.Message;
        _signIn.Content = answer.State == DepotFirstStartState.Offline ? "Try again" : "Sign in";
        if (answer.State != DepotFirstStartState.SignedIn)
        {
            _choices.IsVisible = false;
            return;
        }

        var latest = answer.Backups?.FirstOrDefault();
        _restore.Content = _Choice(
            "Restore my latest backup",
            latest is null ? "There is no backup in your Depot yet." : $"Made {latest.CreatedAt.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.InvariantCulture)}, {_Size(latest.Size)}. The cockpit restarts with it.");
        _restore.IsEnabled = latest is not null;
        _setUp.Content = _Choice("Set up from my profile and organisation", "Installs what your organisation chose for you, after you have seen what that is.");
        _setUp.IsVisible = answer.Manifest is not null;
        _consent.IsVisible = false;
        _outcome.Text = string.Join("\n", answer.Notes ?? []);

        _choices.IsVisible = true;
    }

    private async Task _RestoreAsync()
    {
        if (_answer is not { TargetId: { } targetId, Backups: [var latest, ..] })
        {
            return;
        }

        _restore.IsEnabled = false;
        _setUp.IsEnabled = false;
        _outcome.Text = "Fetching your backup…";
        _outcome.Text = await _host.RestoreBackupAsync(targetId, latest.Id).ConfigureAwait(true);
        _restore.IsEnabled = true;
        _setUp.IsEnabled = true;
    }

    private async Task _SetUpAsync()
    {
        if (_answer?.Manifest is not { } manifest)
        {
            return;
        }

        _restore.IsEnabled = false;
        _setUp.IsEnabled = false;
        _outcome.Text = string.Empty;
        try
        {
            var report = await _host.ApplyManifestAsync(manifest, _AskAsync).ConfigureAwait(true);
            _outcome.Text = string.Join("\n", report);
        }
        finally
        {
            _consent.IsVisible = false;
            _restore.IsEnabled = _answer?.Backups is { Count: > 0 };
            _setUp.IsEnabled = true;
        }
    }

    // The consent screen of manifest-v1 §5.3, in place: what the host says it would set up, and nothing until "Set it up".
    private Task<bool> _AskAsync(IReadOnlyList<string> lines)
    {
        _agreed = new TaskCompletionSource<bool>();
        var agree = new Button { Name = "DepotFirstStartAgree", Content = "Set it up" };
        var cancel = new Button { Content = "Cancel", Margin = new Thickness(8, 0, 0, 0) };
        agree.Click += (_, _) => _agreed.TrySetResult(true);
        cancel.Click += (_, _) => _agreed.TrySetResult(false);

        _consent.Children.Clear();
        _consent.Children.Add(_Hint("This sets up your cockpit:"));
        foreach (var line in lines)
        {
            _consent.Children.Add(new TextBlock { Text = line, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        _consent.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), Children = { agree, cancel } });
        _consent.IsVisible = true;
        return _agreed.Task;
    }

    private static StackPanel _Choice(string title, string detail) => new()
    {
        Spacing = 2,
        Children =
        {
            new TextBlock { Text = title, FontWeight = FontWeight.SemiBold },
            new TextBlock { Text = detail, FontSize = 11, Opacity = 0.75, TextWrapping = TextWrapping.Wrap },
        },
    };

    private static string _Size(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB");

    private static TextBlock _Hint(string text) => new() { Text = text, FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
}

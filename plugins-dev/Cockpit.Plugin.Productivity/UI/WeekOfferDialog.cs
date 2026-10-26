using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.Plugin.Productivity.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Productivity.UI;

internal sealed class WeekOfferDialog : UserControl
{
    private readonly ProductivityController _controller;
    private readonly ICockpitUiHost _host;
    private readonly WeekOfferDraft _draft;
    private readonly DateOnly _weekStart;
    private readonly int _weekNumber;
    private readonly string? _projectId;
    private readonly Button _send = new() { Name = "SendOffer", Content = "Versturen", IsEnabled = false, Classes = { "Accent" } };
    private readonly CheckBox _confirm = new() { Name = "ConfirmOffer", Content = "Ik heb het voorstel en de instructies gecontroleerd" };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private WeekOfferDestination? _selected;

    public WeekOfferDialog(
        ProductivityController controller,
        ICockpitUiHost host,
        WeekOfferOptions options,
        WeekOfferDraft draft,
        DateOnly weekStart,
        int weekNumber,
        string? projectId)
    {
        _controller = controller;
        _host = host;
        _draft = draft;
        _weekStart = weekStart;
        _weekNumber = weekNumber;
        _projectId = projectId;
        _error.Themed(TextBlock.ForegroundProperty, "CockpitStatusErrorBrush");
        _confirm.IsCheckedChanged += (_, _) => _send.IsEnabled = _confirm.IsChecked == true && _selected is not null;
        _send.Click += async (_, _) => await _SendAsync();
        var cancel = new Button { Name = "CancelOffer", Content = "Annuleren", Classes = { "Subtle" } };
        cancel.Click += (_, _) => cancel.FindAncestorOfType<Window>()?.Close();

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("330,*"), ColumnSpacing = 18 };
        var tabs = new TabControl
        {
            Name = "OfferDestinationTabs",
            ItemsSource = new[]
            {
                new TabItem { Header = "Bestaande sessie", Content = _Choices(options.Sessions, options.Remembered, "Er zijn geen agentsessies open.") },
                new TabItem { Header = "Nieuw met profiel", Content = _Choices(options.Profiles, options.Remembered, "Er zijn geen profielen beschikbaar.") },
            },
            SelectedIndex = _selected?.Kind == WeekOfferDestinationKind.Profile ? 1 : 0,
        };
        body.Children.Add(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "1  Kies waar de opdracht heen gaat", FontSize = 15, FontWeight = FontWeight.SemiBold },
                tabs,
                Ui.Card(new StackPanel
                {
                    Spacing = 5,
                    Children =
                    {
                        new TextBlock { Text = "Vaste veiligheidsgrenzen", FontWeight = FontWeight.SemiBold },
                        Ui.Hint("De schemagrens en het akkoord vooraf staan vast in de prompt en zijn niet bewerkbaar."),
                    },
                }, 12),
            },
        });

        var preview = new TextBox
        {
            Name = "PromptPreview",
            Text = draft.Prompt,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(preview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(preview, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var previewPanel = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        previewPanel.Children.Add(new TextBlock { Text = "2  Controleer de prompt", FontSize = 15, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(preview, 1);
        previewPanel.Children.Add(preview);
        Grid.SetColumn(previewPanel, 1);
        body.Children.Add(previewPanel);

        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 10 };
        footer.Children.Add(_confirm);
        Grid.SetColumn(cancel, 1);
        footer.Children.Add(cancel);
        Grid.SetColumn(_send, 2);
        footer.Children.Add(_send);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 12,
            Margin = new Thickness(18),
            Children =
            {
                new TextBlock { Text = $"Geef aan een agent · urenvoorstel week {weekNumber}", FontSize = 19, FontWeight = FontWeight.SemiBold },
                body,
                _error,
                footer,
            },
        };
        Grid.SetRow(body, 1);
        Grid.SetRow(_error, 2);
        Grid.SetRow(footer, 3);

    }

    private Control _Choices(IReadOnlyList<WeekOfferChoice> choices, WeekOfferDestination? remembered, string empty)
    {
        if (choices.Count == 0)
        {
            return Ui.Hint(empty);
        }

        var panel = new StackPanel { Spacing = 6, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var choice in choices)
        {
            var rememberedChoice = remembered is not null
                && remembered.Kind == choice.Destination.Kind
                && string.Equals(remembered.Id, choice.Destination.Id, StringComparison.Ordinal);
            var selectFirst = remembered is null && _selected is null;
            var radio = new RadioButton
            {
                Name = choice.Destination.Kind == WeekOfferDestinationKind.Session ? "OfferSession" : "OfferProfile",
                GroupName = "week-offer-destination",
                IsChecked = rememberedChoice || selectFirst,
                Content = _ChoiceContent(choice),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            radio.IsCheckedChanged += (_, _) =>
            {
                if (radio.IsChecked == true)
                {
                    _Select(choice.Destination);
                }
            };
            panel.Children.Add(radio);
            if (rememberedChoice || selectFirst)
            {
                _Select(choice.Destination);
            }
        }

        return new ScrollViewer { Content = panel, MaxHeight = 350 };
    }

    private static Control _ChoiceContent(WeekOfferChoice choice)
    {
        var details = new List<string>();
        if (string.IsNullOrWhiteSpace(choice.ProfileLabel) == false)
        {
            details.Add(choice.ProfileLabel);
        }

        if (string.IsNullOrWhiteSpace(choice.Status) == false)
        {
            details.Add(_Status(choice.Status));
        }

        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = choice.Destination.Label, FontWeight = FontWeight.SemiBold });
        if (details.Count > 0)
        {
            panel.Children.Add(Ui.Hint(string.Join(" · ", details)));
        }

        return panel;
    }

    private async Task _SendAsync()
    {
        if (_selected is not { } destination || _confirm.IsChecked != true)
        {
            return;
        }

        _send.IsEnabled = false;
        _error.IsVisible = false;
        try
        {
            await _controller.SendWeekOfferAsync(new WeekOfferSendRequest(
                destination,
                _draft.Prompt,
                _weekStart,
                _weekNumber,
                _draft.OwnEntryIds,
                _draft.ProjectCells,
                _projectId,
                _draft.Rows,
                _draft.Schedule));
            _host.ShowToast("Het urenvoorstel is aan de agent gegeven.", PluginToastSeverity.Success);
            this.FindAncestorOfType<Window>()?.Close();
        }
        catch (WeekOfferFinalizationException exception)
        {
            _error.Text = exception.Message;
            _error.IsVisible = true;
            _confirm.IsEnabled = false;
        }
        catch (Exception exception)
        {
            _error.Text = exception.Message;
            _error.IsVisible = true;
            _send.IsEnabled = true;
        }
    }

    private void _Select(WeekOfferDestination destination)
    {
        _selected = destination;
        _send.Content = $"Versturen naar {destination.Label}";
        _send.IsEnabled = _confirm.IsChecked == true;
    }

    private static string _Status(string status) => status switch
    {
        "Busy" => "bezig",
        "Waiting" => "wacht",
        "NeedsAttention" => "aandacht nodig",
        "Done" => "klaar",
        "Idle" => "idle",
        _ => status,
    };
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.HeavyRuns;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Controls;

// AC-1533: ⋯ → Details over a Simple conversation — a session's header or the assistant's.
public partial class ConversationDetailsButton : UserControl
{
    public static readonly StyledProperty<string?> ProjectNameProperty =
        AvaloniaProperty.Register<ConversationDetailsButton, string?>(nameof(ProjectName));

    public string? ProjectName
    {
        get => GetValue(ProjectNameProperty);
        set => SetValue(ProjectNameProperty, value);
    }

    private IMachineResources? _resources;

    public ConversationDetailsButton()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Use(Program.Services?.GetService<IMachineResources>());
        DetachedFromVisualTree += (_, _) => Use(null);
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    internal void Use(IMachineResources? resources)
    {
        if (_resources is not null)
        {
            _resources.Sampled -= _OnSampled;
        }
        _resources = resources;
        if (_resources is not null)
        {
            _resources.Sampled += _OnSampled;
        }
        _RefreshScratch();
    }

    private void _OnSampled() => Dispatcher.UIThread.Post(_RefreshScratch);

    private void _RefreshScratch()
    {
        if (this.FindControl<StackPanel>("DetailsPanel")?.DataContext is not ConversationDetails details)
        {
            return;
        }
        (string Path, long? Bytes) usage = default;
        bool measured = _resources?.ScratchUsage.TryGetValue(details.PaneId, out usage) == true;
        if (this.FindControl<SelectableTextBlock>("ScratchPath") is { } path)
        {
            path.Text = measured ? usage.Path : "Awaiting scratch reading";
        }
        if (this.FindControl<TextBlock>("ScratchSizeText") is { } size)
        {
            size.Text = measured ? DiskStorageStatus._Bytes(usage.Bytes) : "Awaiting scratch reading";
        }
    }

    private void _OnOpening(object? sender, EventArgs e)
    {
        if (this.FindControl<StackPanel>("DetailsPanel") is { } panel)
        {
            panel.DataContext = DataContext switch
            {
                AssistantChatViewModel assistant => assistant.Details(),
                SessionPanelViewModel session => ConversationDetails.For(session, ProjectName, TopLevel.GetTopLevel(this)?.DataContext as CockpitViewModel),
                _ => null,
            };
        }
        _RefreshScratch();
    }

    private void _OnAction(object? sender, RoutedEventArgs e) => this.FindControl<Button>("DetailsButton")?.Flyout?.Hide();
}

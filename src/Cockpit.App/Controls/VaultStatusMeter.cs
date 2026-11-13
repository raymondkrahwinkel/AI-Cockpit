using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.ViewModels;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Controls;

// AC-2347: "Secrets in use: N" in the status bar. Hidden at nought; its panel lists, per session, what the agents hold and revokes it.
internal sealed class VaultStatusMeter : Button
{
    protected override Type StyleKeyOverride => typeof(Button);

    private readonly TextBlock _text = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly MaterialIcon _chevron = new() { Kind = MaterialIconKind.ChevronUp, Width = 10, Height = 10, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _chip = new() { IsVisible = false };
    private readonly TextBlock _chipText = new();
    private VaultOverviewViewModel? _vault;
    private Flyout? _flyout;

    public VaultStatusMeter()
    {
        Classes.Add("statusMeter");
        Padding = new Thickness(8, 2);
        Margin = new Thickness(0, 0, 8, 0);
        IsVisible = false;
        ToolTip.SetTip(this, "Secrets and keys your agents hold — click to see who has what, or revoke it");
        var icon = new MaterialIcon { Kind = MaterialIconKind.KeyChainVariant, Width = 13, Height = 13, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("CockpitStatusBackgroundBrush"));
        _text.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusBackgroundBrush"));
        _chevron.Bind(TemplatedControl.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _chip.Classes.Add("waitingCount");
        _chip.Child = _chipText;
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Spacing = 5,
            Children = { icon, _text, _chip, _chevron },
        };
        DataContextChanged += (_, _) => Use((DataContext as CockpitViewModel)?.Vault);
        DetachedFromVisualTree += (_, _) => _flyout?.Hide();
        Click += (_, _) =>
        {
            if (_flyout is { IsOpen: true })
            {
                _flyout.Hide();
                return;
            }

            var flyout = _flyout = new Flyout { Placement = PlacementMode.Top, Content = _BuildPanel() };
            flyout.Closed += (_, _) => _chevron.Kind = MaterialIconKind.ChevronUp;
            _chevron.Kind = MaterialIconKind.ChevronDown;
            flyout.ShowAt(this);
        };
    }

    // The window hands over its vault overview; the scenes hand over one with made-up facts.
    internal void Use(VaultOverviewViewModel? vault)
    {
        if (_vault is not null)
        {
            _vault.Changed -= _OnChanged;
        }

        _vault = vault;
        if (vault is not null)
        {
            vault.Changed += _OnChanged;
        }

        _Refresh();
    }

    private void _OnChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(_Refresh);

    private void _Refresh()
    {
        IsVisible = _vault?.HasSecretsInUse == true;
        _text.Text = _vault?.MeterText ?? string.Empty;
        _chip.IsVisible = _vault?.HasOpenRequests == true;
        _chipText.Text = _vault?.OpenRequests.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (_flyout is { IsOpen: true } open && open.Content is ScrollViewer)
        {
            open.Content = _BuildPanel();
        }
    }

    private Control _BuildPanel()
    {
        var vault = _vault;
        var close = new Button { Classes = { "Subtle" }, Padding = new Thickness(6, 0), Content = new MaterialIcon { Kind = MaterialIconKind.Close, Width = 12, Height = 12 } };
        ToolTip.SetTip(close, "Close");
        close.Click += (_, _) => _flyout?.Hide();
        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "Secrets in use", FontWeight = FontWeight.SemiBold, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center });

        var summary = new TextBlock { Text = vault?.PanelSummary, FontSize = 14, FontWeight = FontWeight.SemiBold };
        summary.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        var panel = new StackPanel { Spacing = 10, Width = 440, Margin = new Thickness(14, 12) };
        panel.Children.Add(header);
        panel.Children.Add(summary);
        panel.Children.Add(_Rule());
        panel.Children.Add(_Faint("Agents see the name, never the value. A value goes into a process's environment when it starts."));
        foreach (var group in vault?.Groups ?? [])
        {
            panel.Children.Add(_Group(group));
        }

        panel.Children.Add(_Rule());
        var footer = new DockPanel();
        var overview = new Button { Classes = { "Compact" }, Content = "Open vault overview" };
        overview.Click += (_, _) =>
        {
            _flyout?.Hide();
            vault?.OpenOverview?.Invoke();
        };
        DockPanel.SetDock(overview, Dock.Right);
        footer.Children.Add(overview);
        footer.Children.Add(_Faint("Every grant, use and revoke is in the audit log."));
        panel.Children.Add(footer);
        return new ScrollViewer { Content = panel, MaxHeight = 560, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    }

    private Control _Group(VaultSessionGroup group)
    {
        var title = new TextBlock { Text = group.Title, FontSize = 12, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), VerticalAlignment = VerticalAlignment.Center };
        dot.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitStatusBusyBrush"));
        var go = new Button { Classes = { "Compact" }, Content = "Go to session" };
        go.Click += (_, _) =>
        {
            _flyout?.Hide();
            _vault?.GoToSession(group.PaneId);
        };
        DockPanel.SetDock(go, Dock.Right);
        var head = new DockPanel();
        head.Children.Add(go);
        var who = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, title } };
        if (group.Project.Length > 0)
        {
            who.Children.Add(_Faint(group.Project));
        }

        head.Children.Add(who);
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        stack.Children.Add(head);
        foreach (var item in group.Items)
        {
            stack.Children.Add(_Item(item));
        }

        return stack;
    }

    private Control _Item(VaultHeldItem item)
    {
        var name = new TextBlock { Text = item.Name, FontSize = 11.5 };
        name.Bind(TextBlock.FontFamilyProperty, this.GetResourceObservable("CockpitMonoFont"));
        name.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (item.HeldBySession)
        {
            actions.Children.Add(_Revoke("Revoke for this session", item.RevokeSession));
        }

        actions.Children.Add(_Revoke("Revoke everywhere", item.RevokeEverywhere));
        var body = new StackPanel { Spacing = 3, Children = { name, _Faint(item.Detail), actions } };
        var edge = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Padding = new Thickness(10, 0, 0, 0), Margin = new Thickness(15, 0, 0, 0), Child = body };
        edge.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        return edge;
    }

    private static Button _Revoke(string label, Func<Task>? revoke)
    {
        var button = new Button { Classes = { "Compact" }, Content = label, IsEnabled = revoke is not null };
        button.Click += async (_, _) =>
        {
            if (revoke is not null)
            {
                await revoke();
            }
        };
        return button;
    }

    private Border _Rule()
    {
        var rule = new Border { Height = 1 };
        rule.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitHairlineBrush"));
        return rule;
    }

    private TextBlock _Faint(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 10.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        block.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        return block;
    }
}

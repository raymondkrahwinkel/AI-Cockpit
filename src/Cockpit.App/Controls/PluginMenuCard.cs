using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.App.Plugins;
using Cockpit.App.Theming;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Controls;

// AC-1763: what one PluginMenuGroup looks like in the sidebar — a SidebarSection holding the plugin's `.nav` rows, and
// below them each of its sections under a `.sidehead`. The gear (one per plugin, not one per row) and the folded badge
// total sit in the header, as in the mockups' "Companies · 2".
internal static class PluginMenuCard
{
    // `onSettings` is null for a plugin without a settings view, and for the shared "Plugins" card; `isExpanded` and
    // `setExpanded` are the remembered folded state (CockpitViewModel's `plugin:{id}` key).
    public static SidebarSection Build(PluginMenuGroup group, Action? onSettings, bool isExpanded, Action<bool> setExpanded)
    {
        var body = new StackPanel { Spacing = 2 };
        foreach (var button in group.Buttons)
        {
            body.Children.Add(new PluginLauncherButton(button.Title, button.OnInvoke, button.Badge, button.IconKind));
        }

        foreach (var section in group.Sections)
        {
            // The card is already named after the plugin; a section called the same would only say it twice.
            if (!string.Equals(section.Title, group.Title, StringComparison.OrdinalIgnoreCase))
            {
                var head = new TextBlock
                {
                    Text = section.Title,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(10, 8, 10, 4),
                };
                head.Bind(TextBlock.ForegroundProperty, head.GetResourceObservable("CockpitTextFaintBrush"));
                body.Children.Add(head);
            }

            body.Children.Add(section.CreateView());
        }

        var total = new BadgeTotalPill(group.Buttons.Select(button => button.Badge).OfType<SideMenuButtonBadge>().ToList());
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { total } };
        if (onSettings is not null)
        {
            var gear = new Button
            {
                Content = CockpitIcons.Gear(),
                Classes = { "Subtle" },
                Padding = new Thickness(4, 2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(gear, $"{group.Title} settings");
            gear.Click += (_, _) => onSettings();
            trailing.Children.Add(gear);
        }

        var card = new SidebarSection
        {
            Title = group.Title,
            Subtitle = group.PluginId is null ? group.Buttons.Count.ToString() : null,
            Trailing = trailing,
            Content = body,
            IsExpanded = isExpanded,
        };
        total.Folded = !isExpanded;
        card.PropertyChanged += (_, e) =>
        {
            if (e.Property == SidebarSection.IsExpandedProperty)
            {
                total.Folded = !card.IsExpanded;
                setExpanded(card.IsExpanded);
            }
        };
        return card;
    }

    // The sum of the card's numeric badges, shown only while the card is folded: an open card shows each on its own row.
    // Follows the badges live (AC-516), subscribing while attached.
    private sealed class BadgeTotalPill : Border
    {
        private readonly IReadOnlyList<SideMenuButtonBadge> _badges;
        private readonly TextBlock _text = new() { FontSize = 10, FontWeight = FontWeight.SemiBold };
        private bool _folded;

        public BadgeTotalPill(IReadOnlyList<SideMenuButtonBadge> badges)
        {
            _badges = badges;
            VerticalAlignment = VerticalAlignment.Center;
            Padding = new Thickness(7, 1);
            CornerRadius = Application.Current?.TryFindResource("CockpitPillRadius", out var radius) == true && radius is CornerRadius pill
                ? pill
                : new CornerRadius(20);
            Background = ThemeBrush.Resolve("CockpitAccentBrush", "#2563eb");
            _text.Foreground = ThemeBrush.Resolve("CockpitTextOnAccentBrush", "#ffffff");
            Child = _text;
            _Render();
        }

        public bool Folded
        {
            set
            {
                _folded = value;
                _Render();
            }
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            foreach (var badge in _badges)
            {
                badge.Changed += _OnChanged;
            }

            _Render();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            foreach (var badge in _badges)
            {
                badge.Changed -= _OnChanged;
            }

            base.OnDetachedFromVisualTree(e);
        }

        // A plugin updates its counter from wherever it likes; only the UI thread may touch the pill.
        private void _OnChanged() => Dispatcher.UIThread.Post(_Render);

        private void _Render()
        {
            var sum = _badges.Sum(badge => badge.Primary ?? 0);
            _text.Text = sum.ToString(System.Globalization.CultureInfo.InvariantCulture);
            IsVisible = _folded && sum > 0;
        }
    }
}

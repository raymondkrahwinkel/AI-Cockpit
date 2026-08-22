using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.App.Theming;
using Cockpit.Core.Help;
using Cockpit.Core.Markdown;

namespace Cockpit.App.Views;

// The knowledge base (AC-1033). A window of its own so it can stand beside the thing it explains, with the
// four core categories on the left, one search across everything shipped, and a page that can be entered
// half-way down from a `?` elsewhere in the app without leaving the reader wondering where they landed.
internal partial class HelpWindow : Window
{
    private readonly HelpService _help;
    private readonly Dictionary<string, Border> _sections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    private HelpArticle? _article;

    internal HelpWindow(HelpService help)
    {
        _help = help;
        InitializeComponent();
        CockpitWindowChrome.Apply(this);

        SearchBox.TextChanged += (_, _) => _BuildNavigation();
        JumpBannerTop.Click += (_, _) =>
        {
            JumpBanner.IsVisible = false;
            ContentScroll.Offset = new Vector(0, 0);
        };
    }

    // Where the window is pointing. Null shows the overview; an article that is not there, or a section that
    // is not in it, lands on the failure page rather than quietly showing something else.
    public void NavigateTo(HelpAddress? address, string? arrivedFrom = null)
    {
        _sections.Clear();
        ContentHost.Children.Clear();
        JumpBanner.IsVisible = false;

        if (address is null)
        {
            _article = null;
            _BuildOverview();
            _BuildNavigation();
            return;
        }

        var article = _help.Index.Find(address.Article);
        var section = address.Section is null ? null : _help.Index.FindSection(address);
        if (article is null || (address.Section is not null && section is null))
        {
            _article = null;
            _BuildNotFound(address);
            _BuildNavigation();
            return;
        }

        _article = article;
        if (article.Category == HelpCategory.Plugins)
        {
            _expanded.Add(article.Owner.Id);
        }

        _BuildArticle(article);
        _BuildNavigation();

        if (section is not null)
        {
            _ScrollTo(section, article, arrivedFrom);
        }
        else
        {
            ContentScroll.Offset = new Vector(0, 0);
        }
    }

    private void _ScrollTo(HelpSection section, HelpArticle article, string? arrivedFrom)
    {
        if (!_sections.TryGetValue(section.Id, out var target))
        {
            return;
        }

        // Arriving mid-article is the case the landing exists for: nobody chose to be here, so say where the
        // jump came from and offer the way back to the beginning of the page.
        if (arrivedFrom is not null)
        {
            JumpBannerText.Text =
                $"You are here — {arrivedFrom} brought you to “{section.Title}” in {article.Title}.";
            JumpBanner.IsVisible = true;
        }

        target.Background = ThemeBrush.Resolve("CockpitAccentSelectionBrush", "#292563eb");
        Dispatcher.UIThread.Post(() => target.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void _BuildOverview()
    {
        ContentHost.Children.Add(_Title("Documentation"));
        ContentHost.Children.Add(new TextBlock
        {
            Text = "Everything Cockpit and its plugins ship documentation for, in one place. It works with no "
                 + "connection, and it never describes a version you do not have.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
        });

        foreach (var group in _help.Index.Articles.GroupBy(article => article.Category).OrderBy(group => group.Key))
        {
            ContentHost.Children.Add(new TextBlock { Classes = { "navGroup" }, Text = _Label(group.Key).ToUpperInvariant() });

            var cards = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var article in group.OrderBy(article => article.Order).ThenBy(article => article.Title))
            {
                cards.Children.Add(_Card(article));
            }

            ContentHost.Children.Add(cards);
        }
    }

    private Control _Card(HelpArticle article)
    {
        var button = new Button
        {
            Classes = { "Subtle" },
            Width = 248,
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(12, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            BorderBrush = ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39"),
            BorderThickness = new Thickness(1),
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = _Heading(article), FontWeight = FontWeight.SemiBold, FontSize = 13 },
                    new TextBlock
                    {
                        Text = article.Summary ?? string.Empty,
                        IsVisible = article.Summary is not null,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
                    },
                },
            },
        };

        button.Click += (_, _) => NavigateTo(new HelpAddress(article.Id));
        return button;
    }

    // A reference that resolves to nothing is shown, not swallowed: the plugin may be uninstalled, or the
    // section may have been rewritten without the link being updated, and either way the operator clicked
    // something that promised an answer.
    private void _BuildNotFound(HelpAddress address)
    {
        ContentHost.Children.Add(_Title("This page is not here"));
        ContentHost.Children.Add(new TextBlock
        {
            Text = $"Nothing answers to “{address}”. Either the plugin that ships it is not installed, or the "
                 + "section was renamed without this reference being updated.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 12),
            Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
        });

        var back = new Button { Classes = { "Subtle" }, Content = "← Back to the overview", HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => NavigateTo(null);
        ContentHost.Children.Add(back);
    }

    private void _BuildArticle(HelpArticle article)
    {
        ContentHost.Children.Add(_Breadcrumb(article));
        ContentHost.Children.Add(_Title(_Heading(article)));

        var by = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        if (article.Owner.IsThirdParty)
        {
            by.Children.Add(_Badge("third-party", ThemeBrush.Resolve("CockpitCategoryTintPurpleBrush", "#18A67CD8")));
        }

        by.Children.Add(new TextBlock
        {
            Classes = { "crumb" },
            VerticalAlignment = VerticalAlignment.Center,
            Text = article.Owner.Author is { Length: > 0 } author ? $"by {author}" : $"from {article.Owner.Name}",
        });
        ContentHost.Children.Add(by);

        if (article.IsTranslationMissing)
        {
            ContentHost.Children.Add(_Notice("This page has not been translated yet — you are reading the English one."));
        }

        if (article.Lead.Length > 0)
        {
            ContentHost.Children.Add(_Body(article, article.Lead));
        }

        foreach (var section in article.Sections)
        {
            // One control per section, so a deep link can be brought into view and marked. A single view over
            // the whole page would leave "land on the right section" as a guess about pixel offsets.
            var host = new Border { Padding = new Thickness(6, 2), Margin = new Thickness(-6, 0, 0, 0), CornerRadius = new CornerRadius(4) };
            host.Child = _Body(article, section.Markdown);
            _sections[section.Id] = host;
            ContentHost.Children.Add(host);
        }
    }

    private Control _Body(HelpArticle article, string markdown)
    {
        var view = new MarkdownView
        {
            ImageRenderer = block => _Image(article, block),
            LinkHandler = _FollowLink,
        };
        view.Markdown = markdown;
        return view;
    }

    // `help:article#section` moves inside the window; everything else falls through to the browser, which is
    // what the renderer does with a link on its own.
    private bool _FollowLink(string url)
    {
        if (url.StartsWith("help:", StringComparison.OrdinalIgnoreCase))
        {
            // Unconditionally, including at a page that is not there: a reference that breaks has to say so.
            NavigateTo(HelpAddress.Parse(url["help:".Length..]));
            return true;
        }

        var address = _Sibling(url);
        if (address is null || !_help.Index.Contains(address))
        {
            return false;
        }

        NavigateTo(address);
        return true;
    }

    // AC-1042: the plain markdown a page writes for its GitHub reader — `API-REFERENCE.md#icockpithost`, or a
    // bare `#section` — read as a page shipped beside this one, so one spelling serves both readers. Only when
    // it names something actually shipped; anything else is somebody's URL and stays the browser's business.
    private HelpAddress? _Sibling(string url)
    {
        if (url.StartsWith('#'))
        {
            return _article is null ? null : new HelpAddress(_article.Id, url[1..]);
        }

        // Resolved the way a plugin's own reference is, so a page beside this one wins over one of the app's
        // that happens to share its name.
        return HelpAddress.FromSiblingLink(url) is { } link
            ? _help.Resolve(_article?.Owner is { IsCore: false } owner ? owner.Id : null, link.Article, link.Section)
            : null;
    }

    private Control _Image(HelpArticle article, MarkdownBlock block)
    {
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var image = _help.Index.LoadImage(article, block.ImageSource, dark);

        return image.Outcome switch
        {
            HelpImageOutcome.Embedded => _Picture(image.Bytes!, block.ImageAlt),
            HelpImageOutcome.BlockedExternal => _Refused(block.ImageSource),
            _ => _Notice($"This page refers to a picture that was not shipped with it: {block.ImageSource}"),
        };
    }

    private Control _Picture(byte[] bytes, string? caption)
    {
        using var stream = new MemoryStream(bytes);
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 10) };

        try
        {
            panel.Children.Add(new Image
            {
                Source = new Bitmap(stream),
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 560,
            });
        }
        catch (Exception)
        {
            // A file that is not an image the platform can decode is the author's mistake, not a reason to
            // take the page down with it.
            return _Notice("This page ships a picture that could not be displayed.");
        }

        if (caption is { Length: > 0 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = caption,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            });
        }

        return panel;
    }

    // Refused, not fetched, and said out loud. A page from a plugin asking for a picture from somewhere else
    // is a request to a stranger's server at the moment the page opens — which would tell them the operator's
    // address without him doing anything at all.
    private static Control _Refused(string? source) => new Border
    {
        Margin = new Thickness(0, 8, 0, 10),
        Padding = new Thickness(14, 12),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        BorderBrush = ThemeBrush.Resolve("CockpitHairlineBrush", "#2a2f39"),
        Background = ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430"),
        Child = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "🚫 External picture not loaded", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                new TextBlock
                {
                    Text = "This page asked for a picture from outside Cockpit. Fetching it would tell that "
                         + "server you opened this page, so it was not fetched.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
                },
                new SelectableTextBlock
                {
                    Text = source ?? string.Empty,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas, Menlo, monospace"),
                    Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
                },
            },
        },
    };

    private void _BuildNavigation()
    {
        NavHost.Children.Clear();

        if (SearchBox.Text is { Length: > 0 } query)
        {
            _BuildResults(query);
            return;
        }

        NavHost.Children.Add(_NavRow("Overview", 0, _article is null, () => NavigateTo(null)));

        foreach (var group in _help.Index.Articles.GroupBy(article => article.Category).OrderBy(group => group.Key))
        {
            NavHost.Children.Add(new TextBlock
            {
                Classes = { "navGroup" },
                Text = $"{_Label(group.Key).ToUpperInvariant()}   {group.Count()}",
            });

            if (group.Key == HelpCategory.Plugins)
            {
                _BuildPluginBranch(group);
                continue;
            }

            foreach (var article in group.OrderBy(article => article.Order).ThenBy(article => article.Title))
            {
                NavHost.Children.Add(_ArticleRow(article, 1));
            }
        }
    }

    // The only branch the core does not fill: one entry per plugin that ships documentation, named after the
    // plugin, with its own pages under it in the order it chose. Collapsible because at fifteen plugins a flat
    // list of everyone's pages is not a navigation.
    private void _BuildPluginBranch(IEnumerable<HelpArticle> articles)
    {
        foreach (var owner in articles.GroupBy(article => article.Owner).OrderBy(group => group.Key.Name))
        {
            var open = _expanded.Contains(owner.Key.Id);
            var pages = new StackPanel { IsVisible = open, Spacing = 1 };

            var header = _NavRow($"{(open ? "▾" : "▸")}  {owner.Key.Name}", 1, false, null);
            header.Click += (_, _) =>
            {
                if (!_expanded.Remove(owner.Key.Id))
                {
                    _expanded.Add(owner.Key.Id);
                }

                _BuildNavigation();
            };

            NavHost.Children.Add(header);
            foreach (var article in owner.OrderBy(article => article.Order).ThenBy(article => article.Title))
            {
                pages.Children.Add(_ArticleRow(article, 2));
            }

            NavHost.Children.Add(pages);
        }
    }

    private void _BuildResults(string query)
    {
        var hits = _help.Index.Search(query);
        if (hits.Count == 0)
        {
            NavHost.Children.Add(new TextBlock
            {
                Margin = new Thickness(10, 14, 10, 0),
                Text = "Nothing found.",
                FontSize = 12,
                Foreground = ThemeBrush.Resolve("CockpitTextFaintBrush", "#656c78"),
            });
            return;
        }

        foreach (var hit in hits)
        {
            var row = new Button
            {
                Classes = { "Subtle" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 6),
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Classes = { "crumb" }, Text = _Trail(hit) },
                        new TextBlock
                        {
                            Text = hit.Section?.Title ?? hit.Article.Title,
                            FontWeight = FontWeight.SemiBold,
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap,
                        },
                        new TextBlock
                        {
                            Text = hit.Snippet,
                            TextWrapping = TextWrapping.Wrap,
                            FontSize = 11,
                            MaxLines = 3,
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
                        },
                    },
                },
            };

            var address = hit.Address;
            row.Click += (_, _) => NavigateTo(address, "the search results");
            NavHost.Children.Add(row);
        }
    }

    private Button _ArticleRow(HelpArticle article, int depth)
    {
        var row = _NavRow(_Heading(article), depth, _article?.Id == article.Id, () => NavigateTo(new HelpAddress(article.Id)));
        return row;
    }

    private static Button _NavRow(string text, int depth, bool selected, Action? onClick)
    {
        var row = new Button
        {
            Classes = { "Subtle" },
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8 + (depth * 12), 5, 8, 5),
            FontSize = 12,
            Content = text,
            Background = selected
                ? ThemeBrush.Resolve("CockpitAccentSelectionBrush", "#292563eb")
                : Brushes.Transparent,
        };

        if (onClick is not null)
        {
            row.Click += (_, _) => onClick();
        }

        return row;
    }

    private Control _Breadcrumb(HelpArticle article)
    {
        var trail = article.Category == HelpCategory.Plugins
            ? $"Help  /  {_Label(article.Category)}  /  {article.Owner.Name}  /  {article.Title}"
            : $"Help  /  {_Label(article.Category)}  /  {article.Title}";

        return new TextBlock { Classes = { "crumb" }, Text = trail, Margin = new Thickness(0, 0, 0, 6) };
    }

    private static string _Trail(HelpSearchHit hit) =>
        hit.Article.Category == HelpCategory.Plugins
            ? $"{hit.Article.Owner.Name}  ·  {hit.Article.Title}"
            : $"{_Label(hit.Article.Category)}  ·  {hit.Article.Title}";

    private static string _Heading(HelpArticle article) =>
        article.Icon is { Length: > 0 } icon ? $"{icon}  {article.Title}" : article.Title;

    private static TextBlock _Title(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeight.SemiBold,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 0, 0, 4),
    };

    // Takes the brush rather than the key and its fallback: the theme guard exempts a hex only where it is
    // handed straight to ThemeBrush.Resolve, and passing one through another method is exactly the shape it is
    // written to catch.
    private static Control _Badge(string text, IBrush background) => new Border
    {
        Background = background,
        CornerRadius = new CornerRadius(3),
        Padding = new Thickness(6, 2),
        Child = new TextBlock { Classes = { "badge" }, Text = text },
    };

    private static Control _Notice(string text) => new Border
    {
        Margin = new Thickness(0, 4, 0, 10),
        Padding = new Thickness(12, 8),
        CornerRadius = new CornerRadius(6),
        Background = ThemeBrush.Resolve("CockpitInsetBgBrush", "#202430"),
        Child = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = ThemeBrush.Resolve("CockpitTextSecondaryBrush", "#949aa5"),
        },
    };

    private static string _Label(HelpCategory category) => category switch
    {
        HelpCategory.General => "General",
        HelpCategory.System => "System",
        HelpCategory.ExtendingCockpit => "Extending Cockpit",
        _ => "Plugins",
    };
}

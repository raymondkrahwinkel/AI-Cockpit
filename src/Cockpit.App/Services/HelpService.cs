using Avalonia.Controls.ApplicationLifetimes;
using Cockpit.App.Plugins;
using Cockpit.App.Views;
using Cockpit.Core.Help;

namespace Cockpit.App.Services;

// AC-1033: the knowledge base from the app's side — one index over every loaded assembly, and the one window
// that shows it. Built on first use and dropped on `Invalidate`, so an arriving plugin is simply in it.
public sealed class HelpService
{
    private readonly Func<IEnumerable<HelpDocumentSource>> _sources;

    private HelpIndex? _index;
    private HelpWindow? _window;

    public HelpService(PluginManager plugins) => _sources = () => _FromPlugins(plugins);

    // A knowledge base over exactly these assemblies and no plugin manager behind it — what a headless render
    // stages a scene from, and what a test builds a known index with.
    internal HelpService(IEnumerable<HelpDocumentSource> sources) => _sources = () => sources;

    public HelpIndex Index => _index ??= HelpIndex.Build(_sources());

    public void Invalidate() => _index = null;

    public bool Contains(HelpAddress? address) => Index.Contains(address);

    // Resolves an article the way a plugin means it: its own page first, then as written. So a plugin says
    // `"setup"` for its own and `"core-concepts#plugin"` to reach one of ours, without having to know or
    // repeat its own id — which would be a second place its name is written down.
    public HelpAddress Resolve(string? ownerId, string article, string? section)
    {
        var own = new HelpAddress($"{ownerId}/{article}", section);

        return ownerId is { Length: > 0 } && Index.Find(own.Article) is not null ? own : new HelpAddress(article, section);
    }

    // Where "Documentation" on a plugin's settings page should land: that plugin's first page, in the order it
    // chose. Null when it ships none, which is what keeps the link off a page that has nothing behind it.
    public HelpAddress? LandingFor(string ownerId)
    {
        var first = Index.Articles
            .Where(article => string.Equals(article.Owner.Id, ownerId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(article => article.Order)
            .ThenBy(article => article.Title, StringComparer.CurrentCultureIgnoreCase)
            .FirstOrDefault();

        return first is null ? null : new HelpAddress(first.Id);
    }

    // What a `?` says when you hover it, so following one is a decision instead of a guess.
    public string Describe(HelpAddress address)
    {
        var article = Index.Find(address.Article);
        if (article is null)
        {
            return $"Help: {address} (not installed)";
        }

        var section = address.Section is null ? null : Index.FindSection(address);

        return section is null ? $"Help: {article.Title}" : $"Help: {article.Title} → {section.Title}";
    }

    // One window, reused. Opening the help twice from two places is one window being pointed somewhere else,
    // not a second copy of the same thing to keep track of.
    public void Open(HelpAddress? address = null, string? arrivedFrom = null)
    {
        if (_window is { } open)
        {
            open.NavigateTo(address, arrivedFrom);
            open.Activate();
            return;
        }

        var window = new HelpWindow(this);
        window.NavigateTo(address, arrivedFrom);
        window.Closed += (_, _) => _window = null;
        _window = window;

        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            window.Show(owner);
        }
        else
        {
            window.Show();
        }
    }

    // The app's own assembly is registered exactly the way a plugin's is. If the core documentation took a
    // path of its own, this would be a plugin feature with an exception standing beside it.
    private static IEnumerable<HelpDocumentSource> _FromPlugins(PluginManager plugins)
    {
        yield return new HelpDocumentSource(HelpOwner.Core, typeof(HelpService).Assembly);

        foreach (var (discovered, assembly) in plugins.LoadedWithAssemblies)
        {
            yield return new HelpDocumentSource(
                new HelpOwner(discovered.FolderId, discovered.Manifest.Name, discovered.Manifest.Author),
                assembly);
        }
    }
}

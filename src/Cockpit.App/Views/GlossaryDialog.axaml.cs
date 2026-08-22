using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.Core.Help;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Views;

// The five primitives, explained without a browser (AC-512): the guide's own depth lives on the website, but a
// fresh install that cannot reach it yet (AC-510 — no internet, a blocked store) still needs these words.
public partial class GlossaryDialog : Window
{
    // AC-1033: each term's own section in the knowledge base, which is where the same five words are explained
    // at length. The ids are the article's, not this dialog's — one address, checked by the deep-link sweep.
    private static readonly (string Head, HelpAddress Target)[] Terms =
    [
        ("SessionHead", new HelpAddress("core-concepts", "session")),
        ("ProjectHead", new HelpAddress("core-concepts", "project")),
        ("ProfileHead", new HelpAddress("core-concepts", "profile")),
        ("PluginHead", new HelpAddress("core-concepts", "plugin")),
        ("MCPserverHead", new HelpAddress("core-concepts", "mcp-server")),
    ];

    public GlossaryDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        _AddHelpHints();
    }

    // A `?` behind each term, pointing at the section that says more. Each one hides itself when its target is
    // not there, so this dialog reads exactly as it did before if the page is ever removed.
    private void _AddHelpHints()
    {
        if (Program.Services?.GetService<HelpService>() is not { } help)
        {
            return;
        }

        foreach (var (head, target) in Terms)
        {
            if (this.FindControl<StackPanel>(head) is { } host)
            {
                host.Children.Add(new HelpHint(help, target, origin: "a “?” in the glossary"));
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

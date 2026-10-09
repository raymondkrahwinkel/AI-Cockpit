using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.Core.Updates;

namespace Cockpit.App.Views;

// The update banner's "What's new" (AC-1515). Opens at once and fills in when the changelog has been read, so a
// slow or unreachable GitHub costs a line of text in here and nothing in the banner.
public partial class WhatsNewDialog : Window
{
    public WhatsNewDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
    }

    public async Task ShowChangesAsync(string current, string offered, Task<ChangelogResult> changes)
    {
        Range.Text = $"Everything after {current}, up to and including {offered}.";

        var result = await changes;
        if (result.Failure is { } failure)
        {
            Status.Text = $"{failure} Updating is not affected — only this list is.";
            return;
        }

        ChangesText.Markdown = result.Markdown;
        Status.IsVisible = false;
        Changes.IsVisible = true;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}

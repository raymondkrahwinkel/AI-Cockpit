using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>AC-1814: the public sources behind the model facts are credited on their Options page (layer: view tests).</summary>
[Collection("avalonia")]
public class OptionsModelIntelligenceViewTests
{
    [Fact]
    public void TheAttributionOfThePublicSourcesIsShown() => HeadlessAvalonia.Run(() =>
    {
        var dialog = new OptionsDialog { DataContext = new CockpitViewModel() };
        dialog.Show();
        var nav = dialog.GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "CategoryNav");
        nav.SelectedItem = nav.Items.OfType<ListBoxItem>().Single(item => item.Tag as string == "model-intelligence");
        dialog.UpdateLayout();

        var names = dialog.GetVisualDescendants().OfType<Button>().Select(button => button.Content as string).ToArray();
        Assert.Contains("OpenRouter", names);
        Assert.Contains("Artificial Analysis", names);

        dialog.Close();
    });
}

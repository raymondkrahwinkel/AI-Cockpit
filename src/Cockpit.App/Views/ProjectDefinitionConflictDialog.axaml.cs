using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// The conflict window ProjectDialogViewModel.SaveAsync opens over the project editor when a write-back's
// baseChecksum no longer matches (AC-247). Closes with the operator's chosen ProjectDefinitionConflictResolution,
// or null when they cancelled — the same idiom every other dialog here uses.
public partial class ProjectDefinitionConflictDialog : Window
{
    public ProjectDefinitionConflictDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ProjectDefinitionConflictViewModel viewModel)
        {
            return;
        }

        const string title = "Can't save — someone beat you to it";
        Title = title;
        CockpitWindowChrome.Apply(this, title, "Your changes weren't thrown away. Choose what happens to the shared definition.");

        viewModel.CloseRequested += resolution => Close(resolution);
    }
}

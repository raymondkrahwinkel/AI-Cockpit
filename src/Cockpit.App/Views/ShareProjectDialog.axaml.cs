using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-620's confirmation screen. Closes with the project carrying its new binding row, or null when the operator
// cancelled or a publish attempt failed (the error stays visible in the dialog instead of closing it).
public partial class ShareProjectDialog : Window
{
    public ShareProjectDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not ShareProjectDialogViewModel viewModel)
        {
            return;
        }

        Title = viewModel.DialogTitle;
        CockpitWindowChrome.Apply(this, viewModel.DialogTitle, "Two columns: what goes to Depot, what stays here.");

        viewModel.CloseRequested += project => Close(project);
    }
}

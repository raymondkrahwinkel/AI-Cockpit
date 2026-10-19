using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

// AC-1510: closes with the operator's answers per setting key, or null when they chose later.
public partial class SettingsConflictDialog : Window
{
    public SettingsConflictDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not SettingsConflictViewModel viewModel)
        {
            return;
        }

        const string title = "Settings changed on two machines";
        Title = title;
        CockpitWindowChrome.Apply(this, title);

        viewModel.CloseRequested += answers => Close(answers);
    }
}

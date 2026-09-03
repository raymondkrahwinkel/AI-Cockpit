using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cockpit.App.ViewModels.Onboarding;

namespace Cockpit.App.Views.Onboarding;

// The wizard's restore page (AC-1280). Picking the file is a view's job — the same split the options dialog uses
// — everything the choice leads to lives in `ViewModels.Onboarding.RestoreStepViewModel`.
public partial class RestoreStepView : UserControl
{
    public RestoreStepView()
    {
        InitializeComponent();
    }

    private async void OnChooseBackup(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not RestoreStepViewModel viewModel || TopLevel.GetTopLevel(this) is not { } top)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Restore from a backup",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Cockpit backup") { Patterns = ["*.zip"] }],
        });

        if (files.FirstOrDefault()?.TryGetLocalPath() is { Length: > 0 } path)
        {
            await viewModel.RestoreAsync(path);
        }
    }

    private void OnStop(object? sender, RoutedEventArgs e) => (DataContext as RestoreStepViewModel)?.Stop();
}

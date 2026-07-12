using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// Transcript-search dialog (#9). Runs the search on Enter, and the per-row Copy id / Reveal actions (which
/// need the window's clipboard / the OS file explorer) live here rather than in the view model.
/// </summary>
public partial class TranscriptSearchDialog : Window
{
    public TranscriptSearchDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is TranscriptSearchDialogViewModel viewModel)
        {
            viewModel.CloseRequested += Close;
        }
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is TranscriptSearchDialogViewModel viewModel)
        {
            viewModel.SearchCommand.Execute(null);
            e.Handled = true;
        }
    }

    private async void OnCopySessionId(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string sessionId } && Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(sessionId);
        }
    }

    // Opens the transcript file's containing folder in the OS file explorer, selecting the file where the
    // platform supports it. Best-effort — a failure to launch the explorer is swallowed.
    private void OnRevealFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string filePath } || !File.Exists(filePath))
        {
            return;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start(new ProcessStartInfo("open", $"-R \"{filePath}\"") { UseShellExecute = true });
            }
            else
            {
                var folder = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(folder))
                {
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{folder}\"") { UseShellExecute = true });
                }
            }
        }
        catch
        {
            // Revealing is a convenience; if the explorer can't be launched, do nothing.
        }
    }
}

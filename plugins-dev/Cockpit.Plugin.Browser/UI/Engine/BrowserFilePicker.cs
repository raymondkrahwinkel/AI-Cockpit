using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.UI.Engine;

internal static class BrowserFilePicker
{
    internal static async Task<string[]> ChooseAsync(Control owner, BrowserTabFileDialog request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (TopLevel.GetTopLevel(owner)?.StorageProvider is not { } storage)
        {
            return [];
        }
        var filters = request.Filters.Select(filter => new FilePickerFileType(filter)
        {
            Patterns = filter.StartsWith('.') ? ["*" + filter] : null,
            MimeTypes = filter.Contains('/') ? [filter] : null,
        }).ToArray();
        var title = string.IsNullOrEmpty(request.Title) ? null : request.Title;
        using var folder = Path.IsPathFullyQualified(request.DefaultPath) && Path.GetDirectoryName(request.DefaultPath) is { } directory
            ? await storage.TryGetFolderFromPathAsync(directory) : null;
        token.ThrowIfCancellationRequested();
        IStorageItem[] selected;
        switch ((CefFileDialogMode)request.Mode)
        {
            case CefFileDialogMode.Open:
            case CefFileDialogMode.OpenMultiple:
                selected = (await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = title, AllowMultiple = request.Mode == (int)CefFileDialogMode.OpenMultiple, FileTypeFilter = filters,
                    SuggestedStartLocation = folder, SuggestedFileName = Path.GetFileName(request.DefaultPath),
                })).ToArray();
                break;
            case CefFileDialogMode.OpenFolder:
                selected = (await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = folder })).ToArray();
                break;
            case CefFileDialogMode.Save:
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = title, SuggestedFileName = Path.GetFileName(request.DefaultPath), FileTypeChoices = filters, ShowOverwritePrompt = true,
                    SuggestedStartLocation = folder,
                });
                selected = file is null ? [] : [file];
                break;
            default: throw new InvalidDataException("Unknown file dialog mode.");
        }
        try
        {
            token.ThrowIfCancellationRequested();
            return selected.Select(item => item.TryGetLocalPath() ?? throw new InvalidDataException("The browser requires local files.")).ToArray();
        }
        finally
        {
            foreach (var item in selected)
            {
                item.Dispose();
            }
        }
    }
}

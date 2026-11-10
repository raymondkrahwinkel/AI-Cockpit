using System.Text.Json;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed partial class BrowserHostTab
{
    private readonly Dictionary<CancellationTokenSource, TaskCompletionSource> _fileDialogs = [];
    private readonly Dictionary<uint, CefDownloadItemCallback> _downloads = [];
    private long _fileRevision;

    // Callers hold _control. A control change invalidates even a choice already returning through IPC.
    private void _CancelFiles()
    {
        ++_fileRevision;
        foreach (var dialog in _fileDialogs.Keys)
        {
            dialog.Cancel();
        }
        foreach (var download in _downloads.Values)
        {
            download.Cancel();
            download.Dispose();
        }
        _downloads.Clear();
    }

    private async Task _ChooseFilesAsync(BrowserTabFileDialog request, Action<string[]> complete)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_connection.Closed);
        long revision = _fileRevision;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _fileDialogs.Add(cancel, finished);
        string[] paths = [];
        try
        {
            var answer = await _connection.CallAsync(_id, "operator", "tab.files", JsonSerializer.SerializeToElement(request),
                BrowserIpcLimits.Deadline, cancel.Token);
            paths = answer.Deserialize<string[]>() ?? [];
            if (paths.Length > 1024 || paths.Any(path => string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path) == false) ||
                (request.Mode != (int)CefFileDialogMode.OpenMultiple && paths.Length > 1))
            {
                paths = [];
            }
            paths = paths.Select(Path.GetFullPath).ToArray();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or OperationCanceledException or ObjectDisposedException or InvalidOperationException or TimeoutException)
        {
            paths = [];
        }
        finally
        {
            try
            {
                await OnCefAsync(() =>
                {
                    lock (_control)
                    {
                        _fileDialogs.Remove(cancel);
                        complete(_closing || cancel.IsCancellationRequested || _controlOrigin is not null || revision != _fileRevision ? [] : paths);
                    }
                }, CancellationToken.None);
            }
            finally
            {
                finished.TrySetResult();
            }
        }
    }

    private sealed class DownloadHandler(BrowserHostTab owner) : CefDownloadHandler
    {
        protected override bool CanDownload(CefBrowser browser, string url, string requestMethod) =>
            owner._closing == false && owner._controlOrigin is null;

        protected override bool OnBeforeDownload(CefBrowser browser, CefDownloadItem item, string suggestedName, CefBeforeDownloadCallback callback)
        {
            lock (owner._control)
            {
                if (owner._closing || owner._controlOrigin is not null)
                {
                    callback.Dispose();
                    return true;
                }
                _ = owner._ChooseFilesAsync(new BrowserTabFileDialog((int)CefFileDialogMode.Save, string.Empty, Path.GetFileName(suggestedName), []),
                    paths =>
                    {
                        if (paths.Length == 1)
                        {
                            callback.Continue(paths[0], false);
                        }
                        callback.Dispose();
                    });
            }
            return true;
        }

        protected override void OnDownloadUpdated(CefBrowser browser, CefDownloadItem item, CefDownloadItemCallback callback)
        {
            lock (owner._control)
            {
                if (owner._downloads.Remove(item.Id, out var previous))
                {
                    previous.Dispose();
                }
                if (owner._closing || owner._controlOrigin is not null)
                {
                    callback.Cancel();
                    callback.Dispose();
                }
                else if (item.IsInProgress)
                {
                    owner._downloads[item.Id] = callback;
                }
                else
                {
                    callback.Dispose();
                }
            }
        }
    }
}

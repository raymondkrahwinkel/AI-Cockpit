using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cockpit.App.Views.Editor;
using Cockpit.Core.Markdown;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Material.Icons;
using Material.Icons.Avalonia;

namespace Cockpit.App.Files;

// One document stays in this body while the workspace moves between a tab and a window.
internal sealed partial class MarkdownFileWorkspace : UserControl
{
    private readonly IWorkspaceContext _context;
    private readonly FileWorkspaceOpenOptions _options;
    private readonly FileSourceChannelClient _client;
    private readonly Func<Task<bool>> _confirmDiscard;
    private readonly ILogger<MarkdownFileWorkspace> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _state = new() { Name = "FileStatus", FontSize = 12 };
    // Mockup v2: the colour of whoever saved the live file last, before the name.
    private readonly Avalonia.Controls.Shapes.Ellipse _saverDot = new() { Name = "FileSaverDot", Width = 7, Height = 7, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    private readonly TextBlock _message = new() { Name = "FileMessage", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _footer = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _shortcuts = new() { FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _save = new() { Content = "Save", Classes = { "Compact", "Accent" } };
    private readonly Button _edit = new() { Content = "✎ Edit", Classes = { "Compact", "Accent" } };
    private readonly Button _keepOpen = new() { Content = "Keep open", Classes = { "Compact" } };
    private readonly Button _download = new() { Content = "Download", Classes = { "Compact" } };
    private readonly TextBlock _hint = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Preview: a single click on another file replaces this tab. Double-click the tab, Keep open, Edit or start typing to keep it." };
    private readonly Border _hintStrip;
    private IDisposable? _savedSubscription;
    private readonly CheckBox _source = new() { Name = "MarkdownSourceSwitch", Content = "Markdown source", Classes = { "Switch" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Border _statusChip;
    private readonly MarkdownEditor _compare = new() { IsReadOnly = true, IsVisible = false };
    private readonly Border _banner;
    private readonly Border _readOnlyBanner;
    private readonly TextBlock _readOnlyText = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _updates = new() { Content = "Check for updates", Classes = { "Compact" } };
    private readonly Button _copy = new() { Content = "Copy your text", Classes = { "Compact" } };
    private readonly Border _compareBar;
    private readonly TextBlock _bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 12.5 };
    private readonly TextBlock _legend = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _compareButton = new() { Content = "Compare", Classes = { "Compact" } };
    private readonly Button _reload = new() { Classes = { "Compact" } };
    private readonly Button _merge = new() { Content = "Merge", Classes = { "Compact" } };
    private readonly Button _overwrite = new() { Content = "Overwrite with mine", Classes = { "Compact", "Accent" } };
    private readonly CheckBox _compareSource = new() { Content = "As Markdown", Classes = { "Switch" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly string _sourceName;
    private Snapshot? _theirs;
    private bool _comparing;
    private bool? _wasUnsaved;
    private string _savedText = "";
    private string? _version;
    private string? _error;
    private bool _new;
    private bool _loaded;
    private bool _loading;
    private bool _saving;
    private bool _editing;
    private bool _canWrite;
    private bool _conflict;
    private bool _hasBom;
    private bool _closed;
    private Task? _saveTask;
    private LiveTextDocument? _live;
    private LivePresenceFeed? _presence;
    private readonly LivePresenceBar _people = new();
    private string? _openUrl;

    internal MarkdownFileWorkspace(IWorkspaceContext context, FileWorkspaceOpenOptions options,
        FileSourceChannelClient client, Func<Task<bool>> confirmDiscard, ILogger<MarkdownFileWorkspace>? logger = null,
        Func<Task>? checkForUpdates = null)
    {
        _context = context;
        _options = options;
        _client = client;
        _confirmDiscard = confirmDiscard;
        _logger = logger ?? NullLogger<MarkdownFileWorkspace>.Instance;
        _new = options.CreateNew;
        _sourceName = options.Location.Split(" / ")[0] is { Length: > 0 } name ? name : "the source";
        _reload.Content = $"Reload from {_sourceName}";
        Editor = new MarkdownEditor { IsReadOnly = true };
        Editor.UseWorkspaceSourceToggle();

        var location = new TextBlock
        {
            Text = options.Location.Length == 0 ? options.File.Path : $"{options.Location} / {options.File.Path}",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        location.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _statusChip = new Border { Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { _saverDot, _state } }, Padding = new Thickness(8, 2), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        _statusChip.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitPillRadius"));
        var header = new DockPanel { LastChildFill = true };
        foreach (var action in new Control[] { _save, _edit, _download, _keepOpen, _source, _statusChip, _people })
        {
            DockPanel.SetDock(action, Dock.Right);
            action.Margin = new Thickness(6, 0, 0, 0);
            header.Children.Add(action);
        }
        header.Children.Add(location);
        var head = _Strip(header, new Thickness(14, 10));
        var message = _Strip(_message, new Thickness(14, 4));
        _hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _hintStrip = _Strip(_hint, new Thickness(14, 4));
        _message.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        message.Bind(Border.IsVisibleProperty, _message.GetObservable(IsVisibleProperty));
        _shortcuts.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _footer.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        var foot = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_shortcuts, Dock.Right);
        foot.Children.Add(_shortcuts);
        DockPanel.SetDock(_localLines, Dock.Right);
        _localLines.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusWaitingBrush"));
        foot.Children.Add(_localLines);
        foot.Children.Add(_footer);
        var footer = _Strip(foot, new Thickness(14, 5));
        footer.BorderThickness = new Thickness(0, 1, 0, 0);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(message, Dock.Top);
        DockPanel.SetDock(_hintStrip, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.AddRange([_compareButton, _copy, _reload, _merge, _overwrite]);
        DockPanel.SetDock(actions, Dock.Right);
        _banner = new Border
        {
            Padding = new Thickness(14, 8), BorderThickness = new Thickness(0, 0, 0, 1), IsVisible = false,
            Child = new DockPanel { LastChildFill = true, Children = { actions, _bannerText } },
        };
        _banner.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitStatusWaitingRowTintBrush"));
        _banner.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitStatusWaitingRingTintBrush"));
        DockPanel.SetDock(_compareSource, Dock.Right);
        _compareBar = _Strip(new DockPanel { LastChildFill = true, Children = { _compareSource, _legend } }, new Thickness(14, 5));
        _compareBar.IsVisible = false;
        _legend.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        DockPanel.SetDock(_banner, Dock.Top);
        DockPanel.SetDock(_updates, Dock.Right);
        var info = new MaterialIcon { Kind = MaterialIconKind.InformationOutline, Classes = { "errorIcon" }, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(info, Dock.Left);
        _readOnlyBanner = new Border
        {
            Name = "LiveReadOnlyBanner", Classes = { "errorCard", "informational" }, Padding = new Thickness(14, 8),
            CornerRadius = new CornerRadius(0), BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new DockPanel { LastChildFill = true, Children = { _updates, info, _readOnlyText } }, IsVisible = false,
        };
        _readOnlyBanner.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitChromeBgBrush"));
        _readOnlyText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextPrimaryBrush"));
        DockPanel.SetDock(_readOnlyBanner, Dock.Top);
        DockPanel.SetDock(_compareBar, Dock.Top);
        Content = new DockPanel { Children = { head, _readOnlyBanner, _hintStrip, message, _banner, _BuildLiveBanner(), _compareBar, footer, new Grid { Children = { Editor, _compare } } } };
        this.Bind(BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));

        _people.OpenRequested += async (_, _) => await _OpenInSourceAsync().ConfigureAwait(true);
        _save.Click += async (_, _) => await _SaveFromUiAsync().ConfigureAwait(true);
        _edit.Click += (_, _) => BeginEdit();
        _keepOpen.Click += (_, _) => _context.Pin();
        _download.Click += async (_, _) => await _RunUiAsync(_DownloadAsync).ConfigureAwait(true);
        _compareButton.Click += (_, _) => ToggleCompare();
        _updates.Click += async (_, _) => await _RunUiAsync(checkForUpdates ?? (() => Task.CompletedTask)).ConfigureAwait(true);
        _copy.Click += async (_, _) => await _RunUiAsync(async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(Editor.Text).ConfigureAwait(true);
            }
        }).ConfigureAwait(true);
        _reload.Click += async (_, _) => await _RunUiAsync(ReloadFromSourceAsync).ConfigureAwait(true);
        _merge.Click += (_, _) => MergeSourceChanges();
        _overwrite.Click += async (_, _) => await _RunUiAsync(OverwriteWithMineAsync).ConfigureAwait(true);
        _compareSource.IsCheckedChanged += (_, _) => _compare.ShowSource = _compareSource.IsChecked == true;
        _source.IsCheckedChanged += (_, _) => Editor.ShowSource = _source.IsChecked == true;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == MarkdownEditor.ShowSourceProperty)
            {
                _UpdateState();
            }
        };
        Editor.TextChanged += (_, _) =>
        {
            _liveLightTimer.Stop();
            _UpdateState();
        };
        _liveLightTimer.Tick += async (_, _) =>
        {
            _liveLightTimer.Stop();
            await _SaveFromUiAsync().ConfigureAwait(true);
        };
        Editor.EditRequested += (line, text) =>
        {
            if (_live?.Status.State == LiveFileState.ReadOnly)
            {
                return;
            }
            if (_loaded && !_canWrite)
            {
                _error = $"Read-only: you are a Viewer in {(_options.Location.Length == 0 ? _sourceName : _options.Location.Split(" / ")[^1])}";
                _UpdateState();
                return;
            }
            if (!_canWrite || !_loaded)
            {
                return;
            }
            BeginEdit();
            Editor.EditLine(line, atEnd: true);
            var offset = Editor.TextEditor.CaretOffset;
            Editor.TextEditor.Document.Insert(offset, text);
            Editor.TextEditor.CaretOffset = offset + text.Length;
        };
        AddHandler(KeyDownEvent, _OnKeyDown, RoutingStrategies.Tunnel);
        _context.RefreshRequested += _OnRefresh;
        _context.Closed += _OnClosed;
        _context.Pinned += _OnPinned;
        _savedSubscription = _client.Subscribe(FileSourceChannel.Saved, evt => Dispatcher.UIThread.Post(() => _OnFileSaved(evt.Payload)));
        AttachedToVisualTree += (_, _) =>
        {
            if (_editing)
            {
                Editor.TextEditor.TextArea.Focus();
            }
        };
        Ready = _LoadAsync();
    }

    internal MarkdownEditor Editor { get; }
    internal Task Ready { get; }
    internal bool HasConflict => _theirs is not null;
    internal bool IsDirty => _needsFileRefresh || _HasPendingFileText || (_IsLiveLight ? _loaded && Editor.Text != _savedText
        : _live is { } live ? live.HasUnsentText : _loaded && _editing && (_new || Editor.Text != _savedText));
    internal LiveFileStatus? LiveStatus => _live?.Status;

    // A read-only body follows its file: a save from another tab or window, or a click on an already-open file, reloads it quietly.
    internal Task ReloadIfViewingAsync() => _editing || _loading || _saving || _closed || _live is not null ? Task.CompletedTask : _RunUiAsync(_LoadAsync);

    private void _OnFileSaved(JsonElement payload)
    {
        try
        {
            if (payload.Deserialize<FileRef>(FileSourceChannel.Json) == _options.File)
            {
                _ = ReloadIfViewingAsync();
            }
        }
        catch (JsonException) { }
    }

    private void _OnPinned(object? sender, EventArgs e) => _UpdateState();

    private async Task _DownloadAsync()
    {
        if (!_loaded || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = Path.GetFileName(_options.File.Path) }).ConfigureAwait(true);
        if (file is null)
        {
            return;
        }
        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await stream.WriteAsync(new UTF8Encoding(false).GetBytes(_live is null ? _savedText : Editor.Text)).ConfigureAwait(true);
    }

    internal void BeginEdit()
    {
        if (!_loaded || !_canWrite || _closed || _live?.Status.State == LiveFileState.ReadOnly)
        {
            return;
        }
        var wasEditing = _editing;
        var markedLine = Math.Clamp(Editor.MarkedLine, 1, Editor.TextEditor.Document.LineCount);
        _context.Pin();
        _editing = true;
        Editor.IsReadOnly = false;
        if (!wasEditing)
        {
            Editor.EditLine(markedLine, atEnd: true);
        }
        else
        {
            Editor.TextEditor.TextArea.Focus();
        }
        _UpdateState();
    }

    private async Task _LoadAsync()
    {
        if (_loading || _closed)
        {
            return;
        }
        _loading = true;
        _error = null;
        _ClearConflict();
        _UpdateState();
        try
        {
            if (_new)
            {
                var connections = await _client.ConnectionsAsync(_lifetime.Token).ConfigureAwait(true);
                _canWrite = connections.FirstOrDefault(c => c.Id == _options.File.ConnectionId) is { ReadOnly: false };
                if (!_canWrite)
                {
                    throw new FileSourceException(FileSourceErrorKind.Forbidden, "This file connection does not allow a new file.");
                }
                Editor.Text = _options.InitialText;
                Editor.TextEditor.CaretOffset = Editor.Text.Length;
                _savedText = "";
            }
            else
            {
                var snapshot = await _ReadSnapshotAsync().ConfigureAwait(true);
                _hasBom = snapshot.HasBom;
                _canWrite = snapshot.CanWrite;
                if (snapshot.Live && _live is null && await _OpenLiveAsync(snapshot).ConfigureAwait(true))
                {
                    _version = snapshot.Version;
                }
                // Typed into while the read was out: keep that text and the old version, so its save meets the conflict banner.
                else if (!(_loaded && _editing && Editor.Text != _savedText))
                {
                    Editor.Text = snapshot.Text;
                    _savedText = Editor.Text;
                    _version = snapshot.Version;
                }
            }
            _loaded = true;
            _editing = _canWrite && (_new || _options.Edit || _editing);
            Editor.IsReadOnly = !_editing || _live?.Status.State == LiveFileState.ReadOnly;
            if (!_editing)
            {
                Editor.ShowSource = false;
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (Exception error) when (error is FileSourceException or DecoderFallbackException)
        {
            _error = error is FileSourceException ? error.Message : "This file is not valid UTF-8 text.";
            _loaded = false;
            _canWrite = false;
            _editing = false;
            Editor.IsReadOnly = true;
            Editor.Text = "";
        }
        finally
        {
            _loading = false;
            _UpdateState();
        }
    }

    // A live source keeps the text itself; when it cannot open the session, the file is edited and saved as a snapshot.
    private async Task<bool> _OpenLiveAsync(Snapshot read)
    {
        var live = new LiveTextDocument(Editor.TextEditor.Document, _client, _logger);
        try
        {
            var (id, snapshot, wire) = await _client.OpenLiveAsync(_options.File, _lifetime.Token).ConfigureAwait(true);
            if (_closed)
            {
                live.Dispose();
                await _client.CloseLiveAsync(id, CancellationToken.None).ConfigureAwait(true);
                return false;
            }
            Editor.Text = snapshot.Text;
            _savedText = read.Text;
            live.Attach(id, snapshot);
            live.Changed += (_, _) => _UpdateState();
            live.ExternalEdited += (_, edit) => Editor.MarkExternalEdit(edit);
            _live = live;
            if (wire.Peers is { } peers)
            {
                await _ShowPresenceAsync(id, peers, wire.OpenUrl, read).ConfigureAwait(true);
            }
            return true;
        }
        catch (FileSourceException error)
        {
            live.Dispose();
            _logger.LogWarning(error, "Markdown workspace {WorkspaceId} could not open its file live; it saves snapshots instead.", _context.WorkspaceId);
            return false;
        }
    }

    // The source reports who is in the file: draw them, and tell the source where this cursor is.
    private async Task _ShowPresenceAsync(string sessionId, IReadOnlyList<LivePeer> peers, string? openUrl, Snapshot read)
    {
        _openUrl = Uri.TryCreate(openUrl, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
        var saved = read.ModifiedBy is { Length: > 0 } by && read.Modified is { } at ? $"Saved by {by} · {at.ToLocalTime():HH:mm}" : null;
        _people.SetFooter(saved, _openUrl is null ? null : $"Open in {_sourceName} ↗");
        _presence = new LivePresenceFeed(_client, Editor, _people, _logger);
        await _presence.AttachAsync(sessionId, peers).ConfigureAwait(true);
    }

    private async Task _OpenInSourceAsync()
    {
        if (_openUrl is not { } url || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }
        try
        {
            await launcher.LaunchUriAsync(new Uri(url)).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogWarning(error, "Markdown workspace {WorkspaceId} could not open {Source} in the browser.", _context.WorkspaceId, _sourceName);
        }
    }

    private async Task<Snapshot> _ReadSnapshotAsync()
    {
        var (read, live) = await _client.ReadWithLiveAsync(_options.File, _lifetime.Token).ConfigureAwait(true);
        await using var content = read.Content;
        var bytes = await FileSourceChannel.ReadContentAsync(content, FileSourceChannel.MaxContentBytes, _lifetime.Token).ConfigureAwait(true);
        var text = new UTF8Encoding(false, true).GetString(bytes);
        var bom = text.StartsWith('\uFEFF');
        return new Snapshot(bom ? text[1..] : text, bom, read.Version, read.Modified, read.ModifiedBy, read.CanWrite) { Live = live };
    }

    private void _ClearConflict()
    {
        _conflictMine = null;
        _conflictMerged = null;
        _needsFileRefresh = false;
        _fallbackOrigin = null;
        _conflict = false;
        _theirs = null;
        _comparing = false;
    }

    internal async Task SaveAsync()
    {
        if (_saveTask is { } active)
        {
            await active.ConfigureAwait(true);
            return;
        }
        var save = _SaveCoreAsync();
        _saveTask = save;
        try
        {
            await save.ConfigureAwait(true);
        }
        finally
        {
            if (ReferenceEquals(_saveTask, save))
            {
                _saveTask = null;
            }
        }
    }

    private async Task _SaveCoreAsync()
    {
        if (!IsDirty && _live?.Status.RequiresRejoin != true || !_canWrite || _closed || _live is not null && !_IsLiveLight)
        {
            return;
        }
        if (_IsLiveLight)
        {
            await _SaveLiveLightAsync().ConfigureAwait(true);
            return;
        }
        var submitted = Editor.Text;
        _saving = true;
        _ClearConflict();
        _error = null;
        _UpdateState();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(submitted);
            using var content = new MemoryStream(_hasBom ? [0xEF, 0xBB, 0xBF, .. bytes] : bytes, writable: false);
            var result = await _client.WriteAsync(_options.File, content, _version, _new, _lifetime.Token).ConfigureAwait(true);
            if (result.Status == FileWriteStatus.Written)
            {
                _savedText = submitted;
                _version = result.Version;
                _new = false;
            }
            else
            {
                await _CaptureConflictAsync().ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (FileSourceException error) { _error = error.Message; }
        finally
        {
            _saving = false;
            _UpdateState();
        }
    }

    // The save wrote nothing; read what the source holds now so the operator can compare, merge or overwrite.
    private async Task _CaptureConflictAsync()
    {
        try
        {
            _theirs = await _ReadSnapshotAsync().ConfigureAwait(true);
            _conflict = true;
        }
        catch (Exception error) when (error is FileSourceException or DecoderFallbackException)
        {
            _error = "The file changed at the source. Your text has not been saved.";
        }
    }

    internal void ToggleCompare()
    {
        if (_theirs is null)
        {
            return;
        }
        _comparing = !_comparing;
        if (_comparing)
        {
            var who = _theirs.ModifiedBy ?? _sourceName;
            _compare.ShowCompare(LineMerge.Compare(_theirs.Text.Split('\n'), Editor.Text.Split('\n')), who);
            _legend.Text = $"Red: only in {_sourceName}{(_theirs.ModifiedBy is null ? "" : $" ({_theirs.ModifiedBy})")} · Green: only in yours";
        }
        _UpdateState();
    }

    internal async Task ReloadFromSourceAsync()
    {
        if (_theirs is not { } theirs || !await _confirmDiscard().ConfigureAwait(true))
        {
            return;
        }
        _ClearConflict();
        _new = false;
        _hasBom = theirs.HasBom;
        Editor.Text = theirs.Text;
        _savedText = theirs.Text;
        _version = theirs.Version;
        _UpdateState();
    }

    internal void MergeSourceChanges()
    {
        if (_theirs is not { } theirs)
        {
            return;
        }
        var merged = _conflictMine is { } before && _conflictMerged is { } draft
            ? LineMerge.Merge(before.Split('\n'), draft.Split('\n'), Editor.Text.Split('\n'))
            : LineMerge.Merge(_savedText.Split('\n'), theirs.Text.Split('\n'), Editor.Text.Split('\n'));
        _ClearConflict();
        _new = false;
        _hasBom = theirs.HasBom;
        _savedText = theirs.Text;
        _version = theirs.Version;
        Editor.TextEditor.Document.Text = string.Join('\n', merged);
        _UpdateState();
    }

    internal async Task OverwriteWithMineAsync()
    {
        if (_theirs is not { } theirs)
        {
            return;
        }
        _version = theirs.Version;
        _new = false;
        _ClearConflict();
        await SaveAsync().ConfigureAwait(true);
    }

    private async Task _RunUiAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Resolving a save conflict in Markdown workspace {WorkspaceId} failed.", _context.WorkspaceId);
            _error = "The conflict could not be resolved. Your text is still open; try again.";
            _UpdateState();
        }
    }

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            if (_live is null)
            {
                // Key events cannot await; the UI boundary catches and logs unexpected failures.
                _ = _SaveFromUiAsync();
            }
        }
        else if (_live is not null && _editing && !Editor.IsReadOnly && _UndoKey(e) is { } redo)
        {
            e.Handled = true;
            _live.Undo(redo);
        }
    }

    // Ctrl+Z undoes; Ctrl+Y and Ctrl+Shift+Z redo. Null for any other key.
    private static bool? _UndoKey(KeyEventArgs e) => (e.Key, e.KeyModifiers) switch
    {
        (Key.Z, KeyModifiers.Control) => false,
        (Key.Y, KeyModifiers.Control) or (Key.Z, KeyModifiers.Control | KeyModifiers.Shift) => true,
        _ => null,
    };

    private async Task _SaveFromUiAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Saving Markdown workspace {WorkspaceId} failed.", _context.WorkspaceId);
            _error = "The file could not be saved. Your text is still open; try saving again.";
            _UpdateState();
        }
    }

    // Every local edit reaches the source before the tab goes; one that cannot within a few seconds asks first.
    private async Task<bool> _ConfirmLiveCloseAsync()
    {
        if (_live is not { } live)
        {
            return true;
        }
        var flush = live.FlushAsync();
        var flushed = await Task.WhenAny(flush, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(true) == flush && await flush.ConfigureAwait(true);
        return flushed || await _confirmDiscard().ConfigureAwait(true);
    }

    private async Task<bool> _ConfirmCloseAsync()
    {
        if (_saveTask is { } save)
        {
            await save.ConfigureAwait(true);
        }
        if (!IsDirty)
        {
            return true;
        }
        var owner = TopLevel.GetTopLevel(this) as Window
            ?? (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is { IsVisible: false })
        {
            owner.Show();
        }
        return await _confirmDiscard().ConfigureAwait(true);
    }

    private async void _OnRefresh(object? sender, EventArgs e)
    {
        // This event has no Task result; keep exceptions inside its boundary.
        try
        {
            if (_live is not null)
            {
                _live.Resync();
                return;
            }
            if (IsDirty || _saving)
            {
                _error = "Save or close your unsaved changes before reloading this file.";
                _UpdateState();
                return;
            }
            await _LoadAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Refreshing Markdown workspace {WorkspaceId} failed.", _context.WorkspaceId);
            _error = "The file could not be reloaded. Your current text is still open.";
            _UpdateState();
        }
    }

    private void _OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _context.SetCloseGuard(null);
        _context.RefreshRequested -= _OnRefresh;
        _context.Closed -= _OnClosed;
        _context.Pinned -= _OnPinned;
        _savedSubscription?.Dispose();
        _liveLightTimer.Stop();
        _countdown.Stop();
        _presence?.Dispose();
        _live?.Dispose();
        // Cancelled, not disposed: a save still in flight reads _lifetime.Token, which throws once the source is disposed.
        _lifetime.Cancel();
    }

    private void _UpdateState()
    {
        if (_closed)
        {
            return;
        }
        var text = Editor.Text;
        var dirty = IsDirty;
        var live = _live?.Status;
        var readOnly = live?.State == LiveFileState.ReadOnly;
        var notSent = _live?.Failed == true;
        if (live is not null)
        {
            Editor.IsReadOnly = !_editing || live.State == LiveFileState.ReadOnly;
            if (readOnly)
            {
                Editor.ShowSource = false;
            }
            if (readOnly && _live is { HasUnsentText: true } pending)
            {
                _theirs = new Snapshot(pending.SourceText, _hasBom, _version ?? "", null, null, false);
                _conflict = true;
            }
            _context.SetMarker(dirty || notSent || live.State is LiveFileState.Offline or LiveFileState.SignInNeeded ? WorkspaceMarker.Unsaved
                : live.State is LiveFileState.Live or LiveFileState.ReadOnly or LiveFileState.NoSaver ? WorkspaceMarker.Live : null);
            _context.SetCloseGuard(_IsLiveLight || _needsFileRefresh || _HasPendingFileText ? _ConfirmCloseAsync : _ConfirmLiveCloseAsync);
        }
        else
        {
            _context.SetMarker(dirty ? WorkspaceMarker.Unsaved : null);
            _context.SetCloseGuard(dirty || _saving ? _ConfirmCloseAsync : null);
        }
        _edit.IsVisible = _loaded && _canWrite && !_editing && !readOnly;
        _save.IsVisible = _editing && live is null;
        _keepOpen.IsVisible = _context.IsPreview;
        _hintStrip.IsVisible = _context.IsPreview;
        _download.IsVisible = _loaded && (!_editing || readOnly) && !_new;
        _save.IsEnabled = dirty && !_saving;
        _source.IsVisible = _editing && !readOnly;
        _source.IsChecked = Editor.ShowSource;
        _state.Text = _loading ? "Loading…" : _saving ? "Saving…" : _conflict ? $"Changed in {_sourceName}"
            : notSent ? $"Not sent to {_sourceName}"
            : live is not null ? live.State switch
            {
                LiveFileState.Connecting => "Connecting…",
                LiveFileState.Offline => $"Offline · not saved to {_sourceName}",
                LiveFileState.SignInNeeded => $"Not saved to {_sourceName} yet",
                LiveFileState.ReadOnly => live.ReadOnlyReason == LiveReadOnlyReason.Viewer ? "Viewer · read only" : "Read only",
                LiveFileState.NoSaver => $"Not saved to {_sourceName} yet",
                LiveFileState.Stale => "Catching up…",
                LiveFileState.Live when live.Saved is { Name: { } by } saved => saved.At is { } at ? $"Saved by {by} · {at.ToLocalTime():HH:mm}" : $"Saved by {by}",
                LiveFileState.Live when live.Saved is not null => $"Not saved to {_sourceName} yet",
                LiveFileState.Live => "Live",
                _ => live.State.ToString(),
            }
            : !_loaded ? "Unavailable" : !_editing ? (_context.IsPreview ? "Preview · " : "") + (_canWrite ? "read-only" : "Viewer") : dirty ? "Unsaved" : "Saved";
        ToolTip.SetTip(_statusChip, live?.Reason);
        _readOnlyBanner.IsVisible = readOnly && live?.Reason is { Length: > 0 } && live.ReadOnlyReason != LiveReadOnlyReason.Viewer;
        _readOnlyText.Inlines?.Clear();
        if (live?.ReadOnlyReason == LiveReadOnlyReason.SchemaMismatch)
        {
            _readOnlyText.Inlines?.Add(new Run("This note uses a newer Depot editor.") { FontWeight = FontWeight.SemiBold });
            _readOnlyText.Inlines?.Add(new Run(" You can watch it live; editing comes back once Cockpit's Depot plugin is updated."));
        }
        else
        {
            _readOnlyText.Inlines?.Add(new Run(live?.Reason ?? ""));
        }
        _updates.IsVisible = live?.ReadOnlyReason == LiveReadOnlyReason.SchemaMismatch;
        _copy.IsVisible = readOnly && dirty;
        _reload.IsVisible = _merge.IsVisible = _overwrite.IsVisible = _live is null || _IsLiveLight;
        var saverColor = live is { State: LiveFileState.Live, Saved.Name: not null } && Color.TryParse(live.Saved.Color, out var parsed) ? parsed : (Color?)null;
        _saverDot.Fill = saverColor is { } color ? new SolidColorBrush(color) : null;
        _saverDot.IsVisible = saverColor is not null && !_loading && !_saving && !_conflict && !notSent;
        var unsaved = _conflict || (dirty && !_saving) || notSent
            || live?.State is LiveFileState.Offline or LiveFileState.SignInNeeded or LiveFileState.NoSaver
            || live is { State: LiveFileState.Live, Saved: { Name: null } };
        if (_wasUnsaved != unsaved)
        {
            _wasUnsaved = unsaved;
            _statusChip.Bind(Border.BackgroundProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingChipTintBrush" : "CockpitInsetBgBrush"));
            _statusChip.Bind(Border.BorderBrushProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingRingTintBrush" : "CockpitHairlineSoftBrush"));
            _state.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingBrush" : "CockpitTextSecondaryBrush"));
        }
        _banner.IsVisible = _theirs is not null;
        _bannerText.Inlines?.Clear();
        if (_theirs is { } theirs)
        {
            var by = theirs.ModifiedBy is { Length: > 0 } author ? $"by {author}" : "";
            var at = theirs.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
            var detail = string.Join(", ", new[] { by, at }.Where(part => part.Length > 0));
            _bannerText.Inlines?.Add(new Run(readOnly ? "Your text has not been sent." : $"{Path.GetFileName(_options.File.Path)} changed in {_sourceName}") { FontWeight = FontWeight.SemiBold });
            _bannerText.Inlines?.Add(new Run(readOnly ? " Your text is still here; compare or copy it before closing." : $" since you opened it{(detail.Length > 0 ? $" — {detail}" : "")}. Nothing was written; your text is still here."));
        }
        _compareButton.Content = _comparing ? "Back to editing" : "Compare";
        _compareBar.IsVisible = _comparing;
        _compare.IsVisible = _comparing;
        Editor.IsVisible = !_comparing;
        _message.Text = _error;
        _message.IsVisible = _error is not null;
        var version = _version is null ? "New file" : $"Source version {_version[..Math.Min(8, _version.Length)]}";
        _footer.Text = $"{(Editor.ShowSource ? "Markdown source" : "Live")} · {_WordCount(text)} words · {version}";
        _UpdateLiveLight();
        _UpdateLiveBanner();
        _shortcuts.Text = live?.State == LiveFileState.ReadOnly ? "Read-only"
            : _editing ? (live is null ? "Ctrl+S save · " : "") + "Ctrl+/ source · Ctrl+B / I / K"
            : _loaded ? (_canWrite ? (_context.IsPreview ? "Type to edit · Double-click tab to keep" : "Edit or start typing") : "Read-only") : "";
    }

    private sealed record Snapshot(string Text, bool HasBom, string Version, DateTimeOffset? Modified, string? ModifiedBy, bool CanWrite)
    {
        public bool Live { get; init; }
    }

    private Border _Strip(Control child, Thickness padding)
    {
        var strip = new Border { Child = child, Padding = padding, BorderThickness = new Thickness(0, 0, 0, 1) };
        strip.Bind(Border.BackgroundProperty, this.GetResourceObservable("CockpitChromeBgBrush"));
        strip.Bind(Border.BorderBrushProperty, this.GetResourceObservable("CockpitHairlineSoftBrush"));
        return strip;
    }

    private static int _WordCount(string text)
    {
        var count = 0;
        var word = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                word = false;
            }
            else if (!word)
            {
                count++;
                word = true;
            }
        }
        return count;
    }
}

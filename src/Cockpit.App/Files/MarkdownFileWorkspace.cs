using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Views.Editor;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Files;

// One document stays in this body while the workspace moves between a tab and a window.
internal sealed class MarkdownFileWorkspace : UserControl
{
    private readonly IWorkspaceContext _context;
    private readonly FileWorkspaceOpenOptions _options;
    private readonly FileSourceChannelClient _client;
    private readonly Func<Task<bool>> _confirmDiscard;
    private readonly ILogger<MarkdownFileWorkspace> _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TextBlock _state = new() { Name = "FileStatus", FontSize = 12 };
    private readonly TextBlock _message = new() { Name = "FileMessage", TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly TextBlock _footer = new() { FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _shortcuts = new() { FontSize = 11.5, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button _save = new() { Content = "Save", Classes = { "Compact", "Accent" } };
    private readonly Button _edit = new() { Content = "Edit", Classes = { "Compact" } };
    private readonly CheckBox _source = new() { Name = "MarkdownSourceSwitch", Content = "Markdown source", Classes = { "Switch" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
    private readonly Border _statusChip;
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

    internal MarkdownFileWorkspace(IWorkspaceContext context, FileWorkspaceOpenOptions options,
        FileSourceChannelClient client, Func<Task<bool>> confirmDiscard, ILogger<MarkdownFileWorkspace>? logger = null)
    {
        _context = context;
        _options = options;
        _client = client;
        _confirmDiscard = confirmDiscard;
        _logger = logger ?? NullLogger<MarkdownFileWorkspace>.Instance;
        _new = options.CreateNew;
        Editor = new MarkdownEditor { IsReadOnly = true };
        Editor.UseWorkspaceSourceToggle();

        var location = new TextBlock
        {
            Text = options.Location.Length == 0 ? options.File.Path : $"{options.Location} / {options.File.Path}",
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        location.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextSecondaryBrush"));
        _statusChip = new Border { Child = _state, Padding = new Thickness(8, 2), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        _statusChip.Bind(Border.CornerRadiusProperty, this.GetResourceObservable("CockpitPillRadius"));
        var header = new DockPanel { LastChildFill = true };
        foreach (var action in new Control[] { _save, _edit, _source, _statusChip })
        {
            DockPanel.SetDock(action, Dock.Right);
            action.Margin = new Thickness(6, 0, 0, 0);
            header.Children.Add(action);
        }
        header.Children.Add(location);
        var head = _Strip(header, new Thickness(14, 10));
        var message = _Strip(_message, new Thickness(14, 4));
        _message.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitStatusErrorBrush"));
        message.Bind(Border.IsVisibleProperty, _message.GetObservable(IsVisibleProperty));
        _shortcuts.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        _footer.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("CockpitTextFaintBrush"));
        var foot = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_shortcuts, Dock.Right);
        foot.Children.Add(_shortcuts);
        foot.Children.Add(_footer);
        var footer = _Strip(foot, new Thickness(14, 5));
        footer.BorderThickness = new Thickness(0, 1, 0, 0);
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(message, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        Content = new DockPanel { Children = { head, message, footer, Editor } };
        this.Bind(BackgroundProperty, this.GetResourceObservable("CockpitSecondaryBgBrush"));

        _save.Click += async (_, _) => await _SaveFromUiAsync().ConfigureAwait(true);
        _edit.Click += (_, _) => BeginEdit();
        _source.IsCheckedChanged += (_, _) => Editor.ShowSource = _source.IsChecked == true;
        Editor.PropertyChanged += (_, e) =>
        {
            if (e.Property == MarkdownEditor.ShowSourceProperty)
            {
                _UpdateState();
            }
        };
        Editor.TextChanged += (_, _) => _UpdateState();
        Editor.EditRequested += (line, text) =>
        {
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
    internal bool IsDirty => _loaded && _editing && (_new || Editor.Text != _savedText);

    internal void BeginEdit()
    {
        if (!_loaded || !_canWrite || _closed)
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
        _conflict = false;
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
                var read = await _client.ReadAsync(_options.File, _lifetime.Token).ConfigureAwait(true);
                await using var content = read.Content;
                var bytes = await FileSourceChannel.ReadContentAsync(content, FileSourceChannel.MaxContentBytes, _lifetime.Token).ConfigureAwait(true);
                var text = new UTF8Encoding(false, true).GetString(bytes);
                _hasBom = text.StartsWith('\uFEFF');
                Editor.Text = _hasBom ? text[1..] : text;
                _savedText = Editor.Text;
                _version = read.Version;
                _canWrite = read.CanWrite;
            }
            _loaded = true;
            _editing = _canWrite && (_new || _options.Edit || _editing);
            Editor.IsReadOnly = !_editing;
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
        if (!IsDirty || !_canWrite || _closed)
        {
            return;
        }
        var submitted = Editor.Text;
        _saving = true;
        _conflict = false;
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
                _conflict = true;
                _error = "The file changed at the source. Your text has not been saved.";
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

    private void _OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            // Key events cannot await; the UI boundary catches and logs unexpected failures.
            _ = _SaveFromUiAsync();
        }
    }

    private async Task _SaveFromUiAsync()
    {
        try
        {
            await SaveAsync().ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _ = error; // Counterproof: deliberately omit logging the failed keyboard save.
            _error = "The file could not be saved. Your text is still open; try saving again.";
            _UpdateState();
        }
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
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private void _UpdateState()
    {
        if (_closed)
        {
            return;
        }
        var text = Editor.Text;
        var dirty = _loaded && _editing && (_new || text != _savedText);
        _context.SetMarker(dirty ? WorkspaceMarker.Unsaved : null);
        _context.SetCloseGuard(dirty || _saving ? _ConfirmCloseAsync : null);
        _edit.IsVisible = _loaded && _canWrite && !_editing;
        _save.IsVisible = _editing;
        _save.IsEnabled = dirty && !_saving;
        _source.IsVisible = _editing;
        _source.IsChecked = Editor.ShowSource;
        _state.Text = _loading ? "Loading…" : _saving ? "Saving…" : _conflict ? "Save conflict"
            : !_loaded ? "Unavailable" : !_editing ? (_canWrite ? "Read-only" : "Viewer") : dirty ? "Unsaved" : "Saved";
        var unsaved = dirty && !_saving && !_conflict;
        if (_wasUnsaved != unsaved)
        {
            _wasUnsaved = unsaved;
            _statusChip.Bind(Border.BackgroundProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingChipTintBrush" : "CockpitInsetBgBrush"));
            _statusChip.Bind(Border.BorderBrushProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingRingTintBrush" : "CockpitHairlineSoftBrush"));
            _state.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(unsaved ? "CockpitStatusWaitingBrush" : "CockpitTextSecondaryBrush"));
        }
        _message.Text = _error;
        _message.IsVisible = _error is not null;
        var version = _version is null ? "New file" : $"Source version {_version[..Math.Min(8, _version.Length)]}";
        _footer.Text = $"{(Editor.ShowSource ? "Markdown source" : "Live")} · {_WordCount(text)} words · {version}";
        _shortcuts.Text = _editing ? "Ctrl+S save · Ctrl+/ source · Ctrl+B / I / K"
            : _loaded ? (_canWrite ? "Edit or start typing" : "Read-only") : "";
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

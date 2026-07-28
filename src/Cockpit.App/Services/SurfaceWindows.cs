using Avalonia.Controls;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

/// <summary>
/// Opens the windows an operator works *in* — projects, MCP servers, the plugin store, a plugin's own
/// dialog — beside the cockpit instead of on top of it (AC-367). Every session and pane lives in the one
/// main window, so a modal owned by it stops the whole cockpit: an agent asking for consent cannot be
/// answered until the window in front is closed, because the answer is a banner in a pane behind it.
/// <para>
/// The caller still awaits — but how a surface hands its answer back does change, and that is the sharp edge
/// here. Avalonia's <c>Close(result)</c> is not readable off a window shown this way (the field behind it is
/// private and only <c>ShowDialog</c> returns it), so a surface that answers something reads it from its view
/// model at the moment the window closes. <c>Close()</c> raises <c>Closed</c> synchronously, so an answer
/// written by anything that runs later than the handler which closed the window is never seen: the caller
/// gets a cancel. See <see cref="SessionDialogService.ShowNewSessionDialogAsync"/> for the ordering that
/// depends on it, and the tests that hold it there.
/// </para>
/// <para>
/// Owned by the window it is shown over, so Avalonia closes it along with its owner and shutting the
/// cockpit down needs nothing here. Everything runs on the UI thread — a <see cref="Window"/> cannot be
/// built off it — so the registry needs no lock.
/// </para>
/// </summary>
public sealed class SurfaceWindows : ISingletonService
{
    private readonly Dictionary<object, Surface> _open = [];

    /// <summary>
    /// Brings an already-open surface forward and hands back the task its first caller is waiting on, or
    /// null when there is none. Callers ask before building a view model: most surfaces read a store to
    /// populate themselves, and doing that for a window that will not be shown is work for nothing.
    /// </summary>
    public Task? TryActivateAsync(object key)
    {
        if (!_open.TryGetValue(key, out var surface))
        {
            return null;
        }

        surface.Window.Activate();

        return surface.Pending;
    }

    /// <summary>
    /// Takes every open surface off the screen until the returned handle is disposed. The screen lock (AC-5) is
    /// modal over the main window, and modality holds an owner — not that owner's siblings. Every surface is a
    /// sibling, so options, MCP servers, the plugin store and each plugin's window stayed usable behind a locked
    /// cockpit. Hidden rather than closed, so what the operator was filling in is still there afterwards.
    /// </summary>
    public IDisposable HideAll()
    {
        var hidden = _open.Keys.ToList();
        foreach (var key in hidden)
        {
            _Window(key)?.Hide();
        }

        // Restored by key, not by reference: a surface can be closed while it is hidden — an agent finishing a
        // task, a plugin closing its own window — and showing a closed window throws.
        return new Restore(() =>
        {
            foreach (var key in hidden)
            {
                _Window(key)?.Show();
            }
        });
    }

    /// <summary>Shows a surface that answers nothing; the task completes when it closes.</summary>
    public Task ShowAsync(object key, Window surface, Window owner)
    {
        if (TryActivateAsync(key) is { } already)
        {
            return already;
        }

        var completion = new TaskCompletionSource();
        _Track(key, surface, completion.Task, () => completion.TrySetResult());
        surface.Show(owner);

        return completion.Task;
    }

    /// <summary>
    /// Shows a surface that answers something. <paramref name="readResult"/> runs once the window has
    /// closed and reads the answer off wherever the surface left it — its view model, in practice.
    /// </summary>
    public Task<TResult?> ShowAsync<TResult>(object key, Window surface, Window owner, Func<TResult?> readResult)
    {
        if (TryActivateAsync(key) is Task<TResult?> already)
        {
            return already;
        }

        var completion = new TaskCompletionSource<TResult?>();
        _Track(key, surface, completion.Task, () => completion.TrySetResult(readResult()));
        surface.Show(owner);

        return completion.Task;
    }

    // One window per key: two of the same surface edit the same store, and the one saved last wins without
    // saying so. Asking again brings the open one forward, so a second click focuses rather than duplicates.
    private void _Track(object key, Window window, Task pending, Action complete)
    {
        _open[key] = new Surface(window, pending);
        window.Closed += OnClosed;

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;

            // Out of the registry before the awaiting caller is released, never after: a continuation that
            // opens the same surface again — save a project, reopen it — would otherwise be handed back the
            // window that has just closed.
            _open.Remove(key);
            complete();
        }
    }

    private Window? _Window(object key) => _open.TryGetValue(key, out var surface) ? surface.Window : null;

    private sealed record Surface(Window Window, Task Pending);

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

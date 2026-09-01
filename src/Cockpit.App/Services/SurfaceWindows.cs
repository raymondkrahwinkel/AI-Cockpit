using Avalonia.Controls;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// AC-367: opens the windows an operator works *in* (projects, MCP servers, plugin store/dialogs) beside the
// cockpit, since a modal owned by the one main window would block an agent's consent banner behind it. Avalonia's
// `Close(result)` is unreadable this way, so a surface reads its answer from its view model when `Closed` fires.
public sealed class SurfaceWindows : ISingletonService
{
    private readonly Dictionary<object, Surface> _open = [];

    // Swappable so a test can watch which window gets reactivated: Window.Activate() is a silent no-op under the
    // headless backend (SurfaceWindowsTests), so nothing about a real focus change is otherwise observable.
    internal Action<Window> ActivateOwner = window => window.Activate();

    // Brings an already-open surface forward and hands back the task its first caller is waiting on, or
    // null when there is none. Callers ask before building a view model: most surfaces read a store to
    // populate themselves, and doing that for a window that will not be shown is work for nothing.
    public Task? TryActivateAsync(object key)
    {
        if (!_open.TryGetValue(key, out var surface))
        {
            return null;
        }

        surface.Window.Activate();

        return surface.Pending;
    }

    // AC-5: takes every open surface off screen until the returned handle is disposed. The screen lock is modal
    // over the main window only, not its sibling surfaces, so without this they stayed usable behind a locked
    // cockpit. Hidden rather than closed, so what the operator was filling in is still there afterwards.
    public IDisposable HideAll()
    {
        var hidden = _open.Keys.ToList();
        foreach (var key in hidden)
        {
            _Surface(key)?.Window.Hide();
        }

        // Restored by key, not by reference: a surface can be closed while it is hidden — an agent finishing a
        // task, a plugin closing its own window — and showing a closed window throws.
        return new Restore(() =>
        {
            foreach (var key in hidden)
            {
                // With the owner it went down with: Hide() drops Window.Owner, and a parameterless Show() brings
                // the surface back detached from the cockpit it is supposed to sit beside.
                if (_Surface(key) is { } surface)
                {
                    surface.Window.Show(surface.Owner);
                }
            }
        });
    }

    // Shows a surface that answers nothing; the task completes when it closes. Only surfaces come through here —
    // a modal question goes to Window.ShowDialog and never touches this class, which DialogModalitySplitTests pins.
    public Task ShowAsync(object key, Window surface, Window owner)
    {
        if (TryActivateAsync(key) is { } already)
        {
            return already;
        }

        owner = _NotASurface(owner);
        var completion = new TaskCompletionSource();
        _Track(key, surface, owner, completion.Task, () => completion.TrySetResult());
        surface.Show(owner);

        return completion.Task;
    }

    // Shows a surface that answers something. `readResult` runs once the window has
    // closed and reads the answer off wherever the surface left it — its view model, in practice.
    public Task<TResult?> ShowAsync<TResult>(object key, Window surface, Window owner, Func<TResult?> readResult)
    {
        if (TryActivateAsync(key) is Task<TResult?> already)
        {
            return already;
        }

        owner = _NotASurface(owner);
        var completion = new TaskCompletionSource<TResult?>();
        _Track(key, surface, owner, completion.Task, () => completion.TrySetResult(readResult()));
        surface.Show(owner);

        return completion.Task;
    }

    // One window per key: two of the same surface edit the same store, and the one saved last wins without
    // saying so. Asking again brings the open one forward, so a second click focuses rather than duplicates.
    private void _Track(object key, Window window, Window owner, Task pending, Action complete)
    {
        _open[key] = new Surface(window, pending, owner);
        window.Closed += OnClosed;

        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;

            // Out of the registry before the awaiting caller is released, never after: a continuation that
            // opens the same surface again — save a project, reopen it — would otherwise be handed back the
            // window that has just closed.
            _open.Remove(key);
            complete();

            // AC-1017: a modeless owned window does not hand focus back to its owner on its own — without this,
            // the owner's next click only reactivates it instead of doing what was clicked.
            ActivateOwner(owner);
        }
    }

    // Surfaces are siblings of the cockpit, never of each other: Avalonia closes an owner's owned windows along
    // with it, so a second diagram opened while the first was in front died with the one the operator closed.
    // One step out is the whole walk — an owner recorded here came through this same method, so it is never a surface.
    private Window _NotASurface(Window owner) =>
        _open.Values.FirstOrDefault(open => open.Window == owner) is { } surface ? surface.Owner : owner;

    private Surface? _Surface(object key) => _open.TryGetValue(key, out var surface) ? surface : null;

    // The owner is remembered rather than read back off the window: Hide() clears Window.Owner, so after one
    // screen lock every surface would look parentless and _NotASurface would hand a surface back as an owner.
    private sealed record Surface(Window Window, Task Pending, Window Owner);

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

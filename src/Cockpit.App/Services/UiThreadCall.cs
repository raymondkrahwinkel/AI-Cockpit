using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// AC-1138: the one way from a request thread onto the UI thread. Every hop is capped, and past the cap the queued
// work is abandoned instead of applied late — the caller gets `ui_unavailable` and the effect never lands.
// The hop stays on Default priority deliberately (AC-1200): raising it would displace render and input.
internal static class UiThreadCall
{
    // From the measured distribution (AC-1138 §2b, 566 production calls): p50 0.8 ms, p95 1.9 s, p99 4.6 s,
    // max 31.8 s. A cap under p95 would fail normal hiccups, so it sits just above p99.
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    // Inline when the caller is already on the UI thread: no dispatch to pay for, and nothing that could time out.
    internal static Task<T> RunAsync<T>(Func<T> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(work()) : DispatchAsync(work, grace);

    internal static Task RunAsync(Action work, TimeSpan? grace = null) =>
        RunAsync(() => { work(); return true; }, grace);

    internal static Task<T> RunAsync<T>(Func<Task<T>> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? work() : DispatchAsync(work, grace);

    internal static Task RunAsync(Func<Task> work, TimeSpan? grace = null) =>
        RunAsync(async () => { await work().ConfigureAwait(false); return true; }, grace);

    // The blocking form, for the callers whose signature has no await to give. Bounded like the rest, so a
    // request thread parked here is released with an answer rather than held for as long as the UI stays away.
    internal static T Run<T>(Func<T> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? work() : DispatchAsync(work, grace).GetAwaiter().GetResult();

    internal static void Run(Action work, TimeSpan? grace = null) =>
        Run(() => { work(); return true; }, grace);

    // No inline branch, for the sites that documented why they must always marshal (AC-577): there a fast path
    // lets a test pass without proving anything, and turns a missing dispatcher loop into a plausible answer.
    internal static Task<T> DispatchAsync<T>(Func<T> work, TimeSpan? grace = null)
    {
        var abandoned = new StrongBox<bool>();
        var job = Dispatcher.UIThread.InvokeAsync(() => Volatile.Read(ref abandoned.Value) ? default! : work());
        return _WaitAsync(job.GetTask(), abandoned, grace ?? DefaultGrace);
    }

    internal static Task<T> DispatchAsync<T>(Func<Task<T>> work, TimeSpan? grace = null)
    {
        var abandoned = new StrongBox<bool>();
        var job = Dispatcher.UIThread.InvokeAsync(
            () => Volatile.Read(ref abandoned.Value) ? Task.FromResult<T>(default!) : work());
        return _WaitAsync(job, abandoned, grace ?? DefaultGrace);
    }

    internal static Task DispatchAsync(Action work, TimeSpan? grace = null) =>
        DispatchAsync(() => { work(); return true; }, grace);

    internal static Task DispatchAsync(Func<Task> work, TimeSpan? grace = null) =>
        DispatchAsync(async () => { await work().ConfigureAwait(false); return true; }, grace);

    private static async Task<T> _WaitAsync<T>(Task<T> job, StrongBox<bool> abandoned, TimeSpan grace)
    {
        try
        {
            return await job.WaitAsync(grace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The job stays queued and runs whenever the thread frees up; this flag is what keeps it from touching
            // view state by then, and nothing observes what it returns after that (AC-1138 §3.4).
            Volatile.Write(ref abandoned.Value, true);
            throw new UiUnavailableException(grace);
        }
    }
}

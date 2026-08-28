using Avalonia.Threading;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// AC-1138: request-thread hops onto the UI thread are capped. AC-1222 makes the cap's result say whether work
// was stopped while queued (`ui_unavailable`) or may still land after starting (`ui_outcome_unknown`).
// Default priority stays deliberate (AC-1200): raising it would displace render and input.
internal static class UiThreadCall
{
    // AC-1138 measured p50 0.8 ms, p95 1.9 s, p99 4.6 s and max 31.8 s across 566 calls; five seconds avoids
    // treating ordinary p95 hiccups as outages while remaining just above p99.
    internal static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    internal static Task<T> RunAsync<T>(Func<T> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? Task.FromResult(work()) : DispatchAsync(work, grace);

    internal static Task RunAsync(Action work, TimeSpan? grace = null) =>
        RunAsync(() => { work(); return true; }, grace);

    internal static Task<T> RunAsync<T>(Func<Task<T>> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? work() : DispatchAsync(work, grace);

    internal static Task RunAsync(Func<Task> work, TimeSpan? grace = null) =>
        RunAsync(async () => { await work().ConfigureAwait(false); return true; }, grace);

    internal static T Run<T>(Func<T> work, TimeSpan? grace = null) =>
        Dispatcher.UIThread.CheckAccess() ? work() : DispatchAsync(work, grace).GetAwaiter().GetResult();

    // Blocking callers still get the cap, so a request thread is released with an answer instead of waiting for UI recovery.
    internal static void Run(Action work, TimeSpan? grace = null) =>
        Run(() => { work(); return true; }, grace);

    // AC-577: always marshal here; an inline route makes a missing dispatcher loop look like a valid answer in tests.
    internal static Task<T> DispatchAsync<T>(Func<T> work, TimeSpan? grace = null)
    {
        var claim = new UiThreadCallClaim();
        var job = Dispatcher.UIThread.InvokeAsync(() => claim.TryClaimCallback() ? work() : default!);
        return _WaitAsync(job.GetTask(), claim, grace ?? DefaultGrace);
    }

    internal static Task<T> DispatchAsync<T>(Func<T> work, Task deadline, TimeSpan grace)
    {
        var claim = new UiThreadCallClaim();
        var job = Dispatcher.UIThread.InvokeAsync(() => claim.TryClaimCallback() ? work() : default!);
        return WaitForCompletionAsync(job.GetTask(), claim, deadline, grace);
    }

    internal static Task<T> DispatchAsync<T>(Func<Task<T>> work, TimeSpan? grace = null)
    {
        var claim = new UiThreadCallClaim();
        var job = Dispatcher.UIThread.InvokeAsync(() => claim.TryClaimCallback() ? work() : Task.FromResult<T>(default!));
        return _WaitAsync(job, claim, grace ?? DefaultGrace);
    }

    internal static Task DispatchAsync(Action work, TimeSpan? grace = null) =>
        DispatchAsync(() => { work(); return true; }, grace);

    internal static Task DispatchAsync(Func<Task> work, TimeSpan? grace = null) =>
        DispatchAsync(async () => { await work().ConfigureAwait(false); return true; }, grace);

    private static async Task<T> _WaitAsync<T>(Task<T> job, UiThreadCallClaim claim, TimeSpan grace)
    {
        using var deadlineCancellation = new CancellationTokenSource();
        try
        {
            return await WaitForCompletionAsync(job, claim, Task.Delay(grace, deadlineCancellation.Token), grace).ConfigureAwait(false);
        }
        finally
        {
            deadlineCancellation.Cancel();
        }
    }

    internal static async Task<T> WaitForCompletionAsync<T>(Task<T> job, UiThreadCallClaim claim, Task deadline, TimeSpan grace)
    {
        if (await Task.WhenAny(job, deadline).ConfigureAwait(false) == job)
        {
            return await job.ConfigureAwait(false);
        }

        throw claim.TryClaimTimeout()
            ? new UiUnavailableException(grace)
            : new UiOutcomeUnknownException(grace);
    }
}

internal sealed class UiThreadCallClaim
{
    private int _owner;

    internal bool TryClaimCallback() => Interlocked.CompareExchange(ref _owner, 1, 0) == 0;

    internal bool TryClaimTimeout() => Interlocked.CompareExchange(ref _owner, 2, 0) == 0;
}

// AC-1896 spike: one thread with a message pump, the stand-in for Avalonia's dispatcher; TextDocument checks its owner thread.
using System.Collections.Concurrent;

internal sealed class SingleThread : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback, object?)> _queue = [];

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public static T Run<T>(Func<Task<T>> work)
    {
        var context = new SingleThread();
        T result = default!;
        var thread = new Thread(() =>
        {
            SetSynchronizationContext(context);
            var task = work();
            task.ContinueWith(_ => context._queue.CompleteAdding(), TaskScheduler.Default);
            foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
            {
                callback(state);
            }
            result = task.GetAwaiter().GetResult();
        });
        thread.Start();
        thread.Join();
        return result;
    }
}

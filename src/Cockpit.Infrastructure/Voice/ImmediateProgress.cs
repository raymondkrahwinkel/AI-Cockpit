namespace Cockpit.Infrastructure.Voice;

// Reports synchronously on the calling thread, unlike Progress<T> which posts to a captured
// SynchronizationContext (reordering vs. the caller). Voice events already document firing off the UI
// thread with subscribers marshaling themselves, so there is nothing here for a context to buy.
internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

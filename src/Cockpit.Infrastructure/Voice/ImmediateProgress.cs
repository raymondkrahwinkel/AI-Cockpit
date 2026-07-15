namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// Reports on the thread that called <see cref="Report"/>. <see cref="Progress{T}"/> posts to a captured
/// <see cref="SynchronizationContext"/> instead, which reorders steps against the code raising them and turns
/// "did anything report?" into a race with the caller reading it. The voice events are documented as firing off
/// the UI thread anyway — subscribers marshal themselves — so there is nothing here for a context to buy.
/// </summary>
internal sealed class ImmediateProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

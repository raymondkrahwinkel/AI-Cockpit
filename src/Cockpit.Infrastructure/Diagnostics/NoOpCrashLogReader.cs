using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Infrastructure.Diagnostics;

/// <summary>
/// The crash-log reader for a platform the cockpit has no discovery path for. It reports nothing rather than
/// leaving the service unregistered, so the diagnostics panel simply shows "none found" instead of failing to
/// build its view model.
/// </summary>
internal sealed class NoOpCrashLogReader : ICrashLogReader
{
    public IReadOnlyList<CrashLogEntry> RecentEntries(int max) => [];
}

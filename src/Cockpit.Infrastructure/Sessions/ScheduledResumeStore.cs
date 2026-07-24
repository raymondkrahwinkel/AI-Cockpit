using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions;

/// <summary>
/// Persists pending resumes under the <c>scheduledResumes</c> section of <c>cockpit.json</c> (AC-234), the same
/// read-modify-write pattern every other section uses so it leaves the rest of the file untouched.
/// </summary>
internal sealed class ScheduledResumeStore : IScheduledResumeStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ScheduledResumeStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the store at an arbitrary config file path.</summary>
    internal ScheduledResumeStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<ScheduledResume>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);

        return configFile?.ScheduledResumes is not { Count: > 0 } stored
            ? []
            : [.. stored.Select(entry => entry.ToDomain()).OrderBy(resume => resume.DueAt)];
    }

    public Task SaveAsync(IReadOnlyList<ScheduledResume> resumes, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.ScheduledResumes = [.. resumes.Select(ScheduledResumeEntry.FromDomain)],
            cancellationToken);
}

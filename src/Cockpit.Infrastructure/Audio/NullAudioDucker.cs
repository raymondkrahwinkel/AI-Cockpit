using Cockpit.Core.Abstractions.Audio;

namespace Cockpit.Infrastructure.Audio;

internal sealed class NullAudioDucker : ISystemAudioDucker
{
    public Task DuckAsync(double level, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task RestoreAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

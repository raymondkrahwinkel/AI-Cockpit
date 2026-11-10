namespace Cockpit.Core.Abstractions.Audio;

public interface ISystemAudioDucker
{
    /// <summary>Attenuates other sessions on Cockpit's output to at most its session volume times level.</summary>
    Task DuckAsync(double level, CancellationToken cancellationToken = default);

    /// <summary>Restores journalled volumes unless the operator has changed them.</summary>
    Task RestoreAsync(CancellationToken cancellationToken = default);
}

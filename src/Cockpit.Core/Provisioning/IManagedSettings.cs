namespace Cockpit.Core.Provisioning;

/// <summary>
/// AC-1507: the cockpit.json paths a provisioning manifest has locked on this machine, such as <c>Terminal.FontSize</c>.
/// </summary>
public interface IManagedSettings
{
    /// <summary>Each managed path with who manages it, as shown in "Managed by …"; empty when nothing is.</summary>
    Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken = default);
}

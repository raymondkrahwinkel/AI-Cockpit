using System.Diagnostics.CodeAnalysis;

namespace Cockpit.Core.Abstractions;

/// <summary>
/// Hands a web address to the operator's browser through the app's one opener (AC-315, AC-1375).
/// </summary>
public interface IExternalLinkOpener
{
    /// <summary>
    /// Parses <paramref name="url"/> only if it is an absolute http(s) address.
    /// </summary>
    bool TryParseWebAddress(string? url, [NotNullWhen(true)] out Uri? address);

    /// <summary>
    /// Opens <paramref name="address"/>; false, having started nothing, when the browser would not launch.
    /// </summary>
    bool TryOpen(Uri address);
}

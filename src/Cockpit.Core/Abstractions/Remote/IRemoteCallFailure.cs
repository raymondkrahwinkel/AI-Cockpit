using System.Net;

namespace Cockpit.Core.Abstractions.Remote;

/// <summary>
/// A call to a connect server that the server answered with an error status (AC-1651), so the frontend can tell a
/// server that does not know an endpoint yet from any other failure without reaching into Infrastructure.
/// </summary>
public interface IRemoteCallFailure
{
    /// <summary>The HTTP status the server answered with.</summary>
    HttpStatusCode Status { get; }
}

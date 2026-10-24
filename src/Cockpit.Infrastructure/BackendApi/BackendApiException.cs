using System.Net;
using Cockpit.Core.Abstractions.Remote;

namespace Cockpit.Infrastructure.BackendApi;

public sealed class BackendApiException(HttpStatusCode status, string errorCode, string description)
    : Exception(description), IRemoteCallFailure
{
    public HttpStatusCode Status { get; } = status;

    public string ErrorCode { get; } = errorCode;

    public string Description { get; } = description;
}

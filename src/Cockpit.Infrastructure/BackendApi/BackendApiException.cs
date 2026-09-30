using System.Net;

namespace Cockpit.Infrastructure.BackendApi;

public sealed class BackendApiException(HttpStatusCode status, string errorCode, string description)
    : Exception(description)
{
    public HttpStatusCode Status { get; } = status;

    public string ErrorCode { get; } = errorCode;

    public string Description { get; } = description;
}

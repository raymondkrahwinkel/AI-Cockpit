using System.ComponentModel;

namespace Cockpit.Infrastructure.Mcp;

// AC-2282: a stopped launch names its cause to whoever started it, also a remote window that cannot read this log.
// A code and one fixed line per kind of failure: never the exception text, which can carry a URL, header or path.
internal static class GatewayLaunchFailure
{
    public static InvalidOperationException Stopped(Exception exception) =>
        new($"Session could not start because its MCP gateway could not be prepared ({Cause(exception)}). The server log has the details.");

    internal static string Cause(Exception exception) => (exception is AggregateException aggregate ? aggregate.GetBaseException() : exception) switch
    {
        InvalidOperationException { Message: var message } when message.StartsWith("The session MCP", StringComparison.Ordinal)
            || message.StartsWith("The MCP launch", StringComparison.Ordinal) => $"gateway_unavailable: {message.TrimEnd('.')}",
        OperationCanceledException or TimeoutException => "timeout: preparing took too long",
        Win32Exception or FileNotFoundException => "command_not_found: a stdio command could not start",
        HttpRequestException { StatusCode: { } status } => $"upstream_http: HTTP {(int)status} from upstream",
        HttpRequestException => "upstream_unreachable: no answer from upstream",
        IOException or UnauthorizedAccessException => "io_error: a file the gateway needs could not be read",
        var other => $"internal_error: {other.GetType().Name}",
    };
}

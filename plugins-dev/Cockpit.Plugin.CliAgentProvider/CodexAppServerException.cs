namespace Cockpit.Plugin.CliAgentProvider;

/// <summary>
/// Raised when <c>codex app-server</c> answers a request with a JSON-RPC <c>error</c> object, or the stdio
/// stream ends with a request still outstanding — carries the server's raw error text so the driver can
/// surface it as a session error rather than hanging on a reply that will never come.
/// </summary>
internal sealed class CodexAppServerException(string message) : Exception(message);

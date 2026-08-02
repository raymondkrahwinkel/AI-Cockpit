namespace Cockpit.Plugin.CliAgentProvider;

// Raised when `codex app-server` answers a request with a JSON-RPC `error` object, or the stdio
// stream ends with a request still outstanding — carries the server's raw error text so the driver can
// surface it as a session error rather than hanging on a reply that will never come.
internal sealed class CodexAppServerException(string message) : Exception(message);

using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Mcp;
using ModelContextProtocol.Authentication;

namespace Cockpit.Infrastructure.Mcp;

// AC-1571 B2: an interactive sign-in on this machine whose browser step runs on a desktop elsewhere. The SDK here keeps
// the PKCE verifier and the state; the authorization link goes out, and only the redirect's query comes back. Set as
// Current around the sign-in, the authorizer redirects to the desktop's loopback port and waits here instead of a browser.
internal sealed class McpOAuthHandoff(int port)
{
    private static readonly AsyncLocal<McpOAuthHandoff?> CurrentHandoff = new();

    private readonly TaskCompletionSource<Uri> _link = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<AuthorizationResult?> _redirect = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _state;

    public static McpOAuthHandoff? Current
    {
        get => CurrentHandoff.Value;
        set => CurrentHandoff.Value = value;
    }

    public Uri RedirectUri { get; } = McpOAuthLoopback.RedirectUriAt(port);

    // Completes once the SDK has built the authorization link, which carries the challenge but never the verifier.
    public Task<Uri> Link => _link.Task;

    public bool Answered => _redirect.Task.IsCompleted;

    public async Task<AuthorizationResult?> AwaitRedirectAsync(Uri authorizationUri, McpSignInStageRecorder? stageRecorder, CancellationToken cancellationToken)
    {
        _state = McpOAuthLoopback.StateOf(authorizationUri);
        _link.TrySetResult(authorizationUri);
        stageRecorder?.Record(McpSignInStage.BrowserRequested);
        using var registration = cancellationToken.Register(Abandon);
        var redirect = await _redirect.Task.ConfigureAwait(false);
        if (redirect is not null)
        {
            stageRecorder?.Record(McpSignInStage.AuthorizationReturned);
        }

        return redirect;
    }

    // False when nothing waits yet, it was already answered, or the redirect does not carry this link's state: a code
    // from any other request is turned away without ending this one. An error redirect with the state ends it unsigned.
    public bool Submit(string query)
    {
        var (code, state, iss, error) = McpOAuthLoopback.ParseCallback(query);
        if (!_link.Task.IsCompleted || _state is not { } expected || state is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(state)))
        {
            return false;
        }

        return _redirect.TrySetResult(error is null ? new AuthorizationResult { Code = code, State = state, Iss = iss } : null);
    }

    // Ends the wait with no code, so the SDK fails the handshake and the coordinator puts back the token it held.
    public void Abandon() => _redirect.TrySetResult(null);
}

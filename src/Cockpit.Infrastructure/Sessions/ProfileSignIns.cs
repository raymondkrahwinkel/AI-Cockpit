using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1357: a provider sign-in started on this machine for a caller elsewhere, one per profile. Only what the operator
// needs crosses: a step's text with anything token-like hidden, its link, and whether it waits for input. The CLI's
// own error text never does — it is raw stderr and may carry a credential — and neither is it logged.
internal sealed class ProfileSignIns(IProfileLoginStarter starter, ILogger<ProfileSignIns>? logger = null) : ISingletonService
{
    // ILoginFlow does not say how long its code lives. Codex's device code lasts fifteen minutes; a Claude code pasted
    // later than that is stale too, so the flow is ended then rather than left holding a CLI process.
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    // How long a start waits for the CLI's first instruction before it answers without one.
    private static readonly TimeSpan FirstStepWait = TimeSpan.FromSeconds(20);

    private readonly ILogger<ProfileSignIns> _logger = logger ?? NullLogger<ProfileSignIns>.Instance;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ProfileSignIn> _byProfile = new(StringComparer.Ordinal);

    // Null when the profile's provider offers no sign-in without a browser. A running sign-in for the same profile is
    // ended first, so a caller who lost its flow id can always start over.
    public async Task<ProfileSignIn?> StartAsync(SessionProfile profile, Func<ProfileSignIn, Task> ended, CancellationToken cancellationToken)
    {
        if (!starter.CanStartLogin(profile) || starter.StartLogin(profile, CancellationToken.None) is not { } flow)
        {
            return null;
        }

        var signIn = new ProfileSignIn(profile.Label, flow, DateTimeOffset.UtcNow + Lifetime, _logger);
        ProfileSignIn? replaced;
        lock (_gate)
        {
            _byProfile.TryGetValue(profile.Label, out replaced);
            _byProfile[profile.Label] = signIn;
        }

        replaced?.Cancel();
        signIn.Run(ended);
        await signIn.FirstStepAsync(FirstStepWait, cancellationToken).ConfigureAwait(false);
        return signIn;
    }

    public ProfileSignIn? Find(string profile, string flowId)
    {
        lock (_gate)
        {
            return _byProfile.TryGetValue(profile, out var signIn) && string.Equals(signIn.FlowId, flowId, StringComparison.Ordinal)
                ? signIn
                : null;
        }
    }
}

internal sealed class ProfileSignIn
{
    private static readonly Regex UrlPattern = new(@"https?://\S+", RegexOptions.Compiled);

    // Thirty-two or more characters without a break is what an access or refresh token looks like; a device code
    // (XXXX-XXXX) and ordinary prose never are.
    private static readonly Regex TokenLike = new(@"[A-Za-z0-9_\-.~+/=]{32,}", RegexOptions.Compiled);

    private readonly ILoginFlow _flow;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _firstStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile SignInStep? _latest;
    private volatile string _status = "running";

    public ProfileSignIn(string profile, ILoginFlow flow, DateTimeOffset expiresAt, ILogger logger)
    {
        Profile = profile;
        _flow = flow;
        ExpiresAt = expiresAt;
        _logger = logger;
    }

    public string FlowId { get; } = Guid.NewGuid().ToString("N");

    public string Profile { get; }

    public DateTimeOffset ExpiresAt { get; }

    public SignInStep? Latest => _latest;

    // running, succeeded, failed or expired.
    public string Status => _status;

    public string? Error => _status switch
    {
        "failed" => "The sign-in did not complete. Start it again.",
        "expired" => "The sign-in was not finished in time and has been ended. Start it again.",
        _ => null,
    };

    public Task SubmitAsync(string value, CancellationToken cancellationToken) => _flow.SubmitAsync(value, cancellationToken);

    internal static string Redact(string message) => TokenLike.Replace(UrlPattern.Replace(message, "[link]"), "[hidden]");

    // Not awaited: the flow outlives the request that started it. _RunAsync catches everything it can meet.
    internal void Run(Func<ProfileSignIn, Task> ended) => _ = _RunAsync(ended);

    internal Task FirstStepAsync(TimeSpan wait, CancellationToken cancellationToken) =>
        Task.WhenAny(_firstStep.Task, Task.Delay(wait, cancellationToken));

    internal void Cancel() => _cancel.Cancel();

    private async Task _RunAsync(Func<ProfileSignIn, Task> ended)
    {
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        expiry.CancelAfter(ExpiresAt - DateTimeOffset.UtcNow);
        try
        {
            await foreach (var step in _flow.Steps.WithCancellation(expiry.Token).ConfigureAwait(false))
            {
                _latest = new SignInStep(Redact(step.Message), step.LinkToOpen, step.AwaitsInput);
                _firstStep.TrySetResult();
            }

            var result = await _flow.Completion.WaitAsync(expiry.Token).ConfigureAwait(false);
            _status = result.Success ? "succeeded" : "failed";
        }
        catch (OperationCanceledException)
        {
            _status = _cancel.IsCancellationRequested ? "failed" : "expired";
        }
        catch (Exception exception)
        {
            _status = "failed";
            _logger.LogWarning("The sign-in for profile {Profile} failed ({Error}).", Profile, exception.GetType().Name);
        }
        finally
        {
            _firstStep.TrySetResult();
            await _EndAsync(ended).ConfigureAwait(false);
        }
    }

    private async Task _EndAsync(Func<ProfileSignIn, Task> ended)
    {
        try
        {
            await _flow.DisposeAsync().ConfigureAwait(false);
            await ended(this).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Ending the sign-in for profile {Profile} failed ({Error}).", Profile, exception.GetType().Name);
        }
    }
}

// One instruction as it may cross the connection: the redacted text, the link to open, and whether input is awaited.
internal sealed record SignInStep(string Message, Uri? Url, bool AwaitsInput);

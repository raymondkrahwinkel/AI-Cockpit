using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Profiles;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Sessions;

// AC-1357: a provider sign-in started on this machine for a caller elsewhere, one per profile. An allowlist crosses:
// the link, a device code of the narrow XXXX-XXXX shape, whether input is awaited, and host text. The CLI's own
// output never does, not even masked — stdout and stderr may carry a credential — and neither is it logged.
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

    // A device code as Codex and OpenAI print one (QX7K-2M9P): two short upper-case groups. Nothing longer passes.
    private static readonly Regex DeviceCode = new(@"\b[A-Z0-9]{4,5}-[A-Z0-9]{4,5}\b", RegexOptions.Compiled);

    // Codex colours its code and link; the escapes would sit against them and break both matches.
    private static readonly Regex AnsiEscape = new(@"\x1b\[[0-9;]*m", RegexOptions.Compiled);

    private readonly ILoginFlow _flow;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancel = new();
    private readonly TaskCompletionSource _firstStep = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Uri? _url;
    private volatile string? _code;
    private volatile bool _awaitsInput;
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

    public Uri? Url => _url;

    public string? Code => _code;

    public bool AwaitsInput => _status == "running" && _awaitsInput;

    // The host's own words for where the flow stands; the CLI's are never passed on.
    public string Message => _status switch
    {
        "succeeded" => "Signed in. The credential stays on this machine.",
        "running" when AwaitsInput => "Open the link, sign in, and paste the code the page shows you.",
        "running" when _code is not null => "Open the link and enter the code there.",
        "running" when _url is not null => "Open the link to sign in.",
        "running" => "Waiting for the provider's sign-in to start.",
        _ => "The sign-in has ended.",
    };

    // running, succeeded, failed, expired, or replaced by a newer start for the same profile.
    public string Status => _status;

    public string? Error => _status switch
    {
        "failed" => "The sign-in did not complete. Start it again.",
        "replaced" => "A newer sign-in for this profile has replaced this one.",
        "expired" => "The sign-in was not finished in time and has been ended. Start it again.",
        _ => null,
    };

    public Task SubmitAsync(string value, CancellationToken cancellationToken) => _flow.SubmitAsync(value, cancellationToken);

    internal static string? DeviceCodeIn(string message) =>
        DeviceCode.Match(UrlPattern.Replace(AnsiEscape.Replace(message, " "), " ")) is { Success: true } match ? match.Value : null;

    private static Uri? _Clean(Uri? link) =>
        link is null || !Uri.TryCreate(AnsiEscape.Replace(link.OriginalString, ""), UriKind.Absolute, out var clean) ? link : clean;

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
                _url = _Clean(step.LinkToOpen) ?? _url;
                _code = DeviceCodeIn(step.Message) ?? _code;
                _awaitsInput = step.AwaitsInput;
                _firstStep.TrySetResult();
            }

            var result = await _flow.Completion.WaitAsync(expiry.Token).ConfigureAwait(false);
            _status = result.Success ? "succeeded" : "failed";
        }
        catch (OperationCanceledException)
        {
            _status = _cancel.IsCancellationRequested ? "replaced" : "expired";
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

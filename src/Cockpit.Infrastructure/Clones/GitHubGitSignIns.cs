using System.ComponentModel;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Infrastructure.Sessions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Infrastructure.Clones;

internal sealed record GitHubGitStanding(bool Available, bool SignedIn, string? User, string Source, string Message);

// A single bounded GitHub login on this host, using the same lifecycle and allowlist as provider sign-ins.
internal sealed class GitHubGitSignIns(ILogger<GitHubGitSignIns>? logger = null) : ISingletonService, IHostedService
{
    private static readonly Regex Account = new(@"Logged in to github\.com account ([A-Za-z0-9][A-Za-z0-9-]{0,38})(?:\s|$)", RegexOptions.Compiled);
    private readonly ILogger _logger = logger ?? NullLogger<GitHubGitSignIns>.Instance;
    private readonly SemaphoreSlim _gate = new(1);
    private ProfileSignIn? _current;

    internal static string? EnvironmentSource =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GH_TOKEN")) ? "GH_TOKEN" :
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_TOKEN")) ? "GITHUB_TOKEN" : null;

    public async Task<GitHubGitStanding> StatusAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var result = await GitHubGitSignInFlow.RunCommandAsync(["auth", "status", "--hostname", "github.com"], deadline.Token).ConfigureAwait(false);
            var source = EnvironmentSource;
            var signedIn = result.ExitCode == 0;
            var match = Account.Match(result.Output);
            return new GitHubGitStanding(true, signedIn, signedIn && match.Success ? match.Groups[1].Value : null,
                source ?? (signedIn ? "gh" : "none"),
                source is not null ? $"GitHub uses {source} from the server environment. Change that environment to replace or remove it." :
                signedIn ? "GitHub is signed in on this server." : "Sign in to GitHub on this server to clone private repositories.");
        }
        catch (Win32Exception)
        {
            return new GitHubGitStanding(false, false, null, EnvironmentSource ?? "none", "GitHub CLI is not available on this server.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new GitHubGitStanding(true, false, null, EnvironmentSource ?? "none", "GitHub sign-in status could not be checked on this server.");
        }
    }

    public async Task<ProfileSignIn> StartAsync(bool withToken, Func<ProfileSignIn, Task> ended, CancellationToken cancellationToken)
    {
        ProfileSignIn signIn;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _CancelAsync("replaced").ConfigureAwait(false);
            signIn = new ProfileSignIn("github", new GitHubGitSignInFlow(withToken), DateTimeOffset.UtcNow + ProfileSignIns.Lifetime, _logger);
            Volatile.Write(ref _current, signIn);
            signIn.Run(ended);
        }
        finally
        {
            _gate.Release();
        }

        await signIn.FirstStepAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return signIn;
    }

    public ProfileSignIn? Find(string flowId) => Volatile.Read(ref _current) is { } current && current.FlowId == flowId ? current : null;

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _CancelAsync("stopped").ConfigureAwait(false);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await GitHubGitSignInFlow.RunCommandAsync(["auth", "logout", "--hostname", "github.com"], deadline.Token, "").ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _CancelAsync("stopped").WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task _CancelAsync(string status)
    {
        if (_current is { Status: "running" } current)
        {
            current.Cancel(status);
            await current.Ended.ConfigureAwait(false);
        }
    }
}

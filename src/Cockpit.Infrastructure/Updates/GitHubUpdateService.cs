using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Updates;

/// <summary>
/// Asks GitHub whether a newer cockpit exists (#71). The releases API, unauthenticated — this repository's releases
/// are public, and asking a operator for a token to be told about an update would be a strange bargain.
/// <para>
/// It reads; it never writes. No download, no install, no replacing a running application: that is a promise this
/// project cannot keep on three platforms without code signing, and an updater that half-keeps it is worse than a
/// link the operator clicks themselves.
/// </para>
/// <para>
/// A check that fails — no network, a rate limit, GitHub having a bad morning — returns a failure and says so. The
/// tempting alternative, reporting "you are up to date", is a lie the operator has every reason to believe.
/// </para>
/// </summary>
internal sealed partial class GitHubUpdateService(HttpClient http, ILogger<GitHubUpdateService> logger) : IUpdateService, ISingletonService
{
    private const string Releases = "https://api.github.com/repos/raymondkrahwinkel/AI-Cockpit/releases";
    private const string NightlyTag = "nightly";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    public (string Version, string Commit) Current { get; } = _Read(typeof(GitHubUpdateService).Assembly);

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Patience);

            var releases = await _FetchAsync(timeout.Token);

            // The newest build the operator has asked to hear about. Stable means tagged releases only; a cockpit
            // that quietly moved onto last night's main is not one you can trust with a day's work.
            var candidates = releases
                .Where(release => channel == UpdateChannel.Nightly || !release.IsPrerelease)
                .OrderByDescending(release => release.PublishedAt)
                .ToList();

            var newer = candidates.FirstOrDefault(release => UpdateComparison.IsNewer(release, Current.Version, Current.Commit));

            return newer is null ? UpdateCheckResult.UpToDate : new UpdateCheckResult(newer, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckResult.Failed("GitHub did not answer in time.");
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "The update check failed.");

            return UpdateCheckResult.Failed($"The update check failed: {exception.Message}");
        }
    }

    private async Task<IReadOnlyList<AppRelease>> _FetchAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Releases}?per_page=10");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        // GitHub refuses a request with no user agent, which is a failure that reads like a bug for the rest of your
        // life if you do not know it.
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AI-Cockpit", Current.Version));

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return document.RootElement
            .EnumerateArray()
            .Where(element => !_Bool(element, "draft"))
            .Select(_ToRelease)
            .ToList();
    }

    private static AppRelease _ToRelease(JsonElement element)
    {
        var tag = _String(element, "tag_name");
        var prerelease = _Bool(element, "prerelease") || string.Equals(tag, NightlyTag, StringComparison.OrdinalIgnoreCase);
        var body = _String(element, "body");

        return new AppRelease(
            prerelease ? string.Empty : tag,
            _Commit(element, body),
            _String(element, "name") is { Length: > 0 } name ? name : tag,
            body,
            _String(element, "html_url"),
            element.TryGetProperty("published_at", out var published) && published.ValueKind == JsonValueKind.String
                ? published.GetDateTimeOffset()
                : DateTimeOffset.MinValue,
            prerelease);
    }

    // A nightly's identity is the commit it was built from, and the workflow tags it with that commit
    // (gh release create --target "$GITHUB_SHA"), so target_commitish carries the sha rather than a branch name.
    // The body names it too; that is the fallback, for a release made some other way.
    private static string _Commit(JsonElement element, string body)
    {
        var commitish = _String(element, "target_commitish");
        if (commitish.Length is >= 7 and <= 40 && commitish.All(Uri.IsHexDigit))
        {
            return commitish;
        }

        var match = Sha().Match(body);

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // What this build is. The version carries the semver; SourceRevisionId appends "+<sha>", which is how a nightly
    // knows which commit it is.
    private static (string Version, string Commit) _Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;

        var plus = informational.IndexOf('+');

        return plus < 0
            ? (informational, string.Empty)
            : (informational[..plus], informational[(plus + 1)..]);
    }

    private static string _String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool _Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    [GeneratedRegex(@"\bcommit `?([0-9a-f]{7,40})`?")]
    private static partial Regex Sha();
}

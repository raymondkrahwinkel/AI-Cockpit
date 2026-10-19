using System.Security.Cryptography;
using System.Text;
using System.Web;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Provisioning;

// A validated manifest for the applier (DB-2.5b), or why there is none. `Failure` never carries the one-time token.
// `Report` is what the applier did with it, line by line (manifest-v1 §4.4), in the reader's note shape.
public sealed record ProvisioningOutcome(ProvisionManifest? Manifest, IReadOnlyList<ProvisionNote> Skipped, string? Failure, ProvisionToken? Token = null)
{
    public IReadOnlyList<ProvisionNote> Report { get; init; } = [];

    // Where the manifest came from, redacted; kept in the applier's record (AC-1507).
    public string? Source { get; init; }
}

// AC-1506: the URL's one-time token, held in memory between reading and applying. Printed as its hash only, so a
// log line or exception that names the outcome never carries it.
public sealed record ProvisionToken(string Value, string Hash)
{
    public override string ToString() => $"{nameof(ProvisionToken)} {{ Hash = {Hash} }}";
}

// AC-1505 (DB-2.5a): route D's way in, before plugins load. Fetches a manifest over HTTPS only and hands back the
// validated object. The URL's one-time token is kept in memory, never shown, and stored only as a hash once used.
public static class ProvisioningEntry
{
    public const string Argument = "--provision";

    public const string ReapplyArgument = "--provision-reapply";

    public const string UrlVariable = "COCKPIT_PROVISION_URL";

    public const string UrlFileVariable = "COCKPIT_PROVISION_URL_FILE";

    private const string TokenParameter = "t";

    private const string UsedTokensFile = "provision-tokens-used.txt";

    private const int MaxManifestBytes = 1024 * 1024;

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    public static string? UrlFromArguments(IReadOnlyList<string> args)
    {
        var index = args.ToList().IndexOf(Argument);
        return index < 0 ? null : args.ElementAtOrDefault(index + 1) ?? string.Empty;
    }

    // The variable, else the file it names (a container secret). Read before the environment scrub removes both.
    public static string? UrlFromEnvironment() =>
        Environment.GetEnvironmentVariable(UrlVariable) is { Length: > 0 } url
            ? url
            : Environment.GetEnvironmentVariable(UrlFileVariable) is { Length: > 0 } file ? File.ReadAllText(file).Trim() : null;

    // How the source may be shown anywhere: scheme, host and path. The query, where the token rides, and user info never.
    public static string Redact(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : "(an unreadable URL)";

    public static IEnumerable<string> RedactArguments(IReadOnlyList<string> args) =>
        args.Select((arg, index) => index > 0 && args[index - 1] == Argument ? Redact(arg) : arg);

    // AC-1506: reading, then the applier's first phase. Both run before any plugin loads.
    public static async Task<ProvisioningOutcome?> ProvisionAsync(string url, bool reapply, string stateRoot, ILogger logger, CancellationToken cancellationToken = default)
    {
        var read = await RunAsync(url, reapply, stateRoot, logger, cancellationToken: cancellationToken);
        return read is { Manifest: not null, Failure: null }
            ? await ProvisionManifestApplier.ApplyAsync(read, stateRoot, logger, cancellationToken)
            : read;
    }

    // Null when there is nothing to do: the state root is already set up and no reapply was asked for.
    public static async Task<ProvisioningOutcome?> RunAsync(
        string url,
        bool reapply,
        string stateRoot,
        ILogger logger,
        HttpMessageHandler? handler = null,
        CancellationToken cancellationToken = default)
    {
        var source = Redact(url);
        if (File.Exists(CockpitConfigPath.For(stateRoot)) && !reapply)
        {
            logger.LogInformation("Provisioning from {Source} skipped: this state is already set up. Start with {Reapply} to apply it again.", source, ReapplyArgument);
            return null;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !_Trusted(uri))
        {
            return _Failed(logger, $"Provisioning needs an https:// address, and {source} is not one, so nothing was fetched.");
        }

        var token = HttpUtility.ParseQueryString(uri.Query)[TokenParameter];
        var tokenHash = string.IsNullOrEmpty(token) ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var usedTokensPath = Path.Combine(stateRoot, UsedTokensFile);
        if (tokenHash is not null && File.Exists(usedTokensPath) && (await File.ReadAllLinesAsync(usedTokensPath, cancellationToken)).Contains(tokenHash))
        {
            return _Failed(logger, $"The provisioning link for {source} has already been used. Ask for a new one.");
        }

        string json;
        try
        {
            using var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
            client.Timeout = FetchTimeout;
            client.MaxResponseContentBufferSize = MaxManifestBytes;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("application/vnd.cockpit.provision+json; v=1");
            request.Headers.Accept.ParseAdd("application/json; q=0.9");
            using var response = await client.SendAsync(request, cancellationToken);
            if (!_Trusted(response.RequestMessage?.RequestUri ?? uri))
            {
                return _Failed(logger, $"{source} redirected away from https, so the manifest was not read.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return _Failed(logger, $"{source} answered {(int)response.StatusCode} {response.ReasonPhrase}, so there is no manifest.");
            }

            if (response.Content.Headers.ContentType?.MediaType is not ("application/vnd.cockpit.provision+json" or "application/json"))
            {
                return _Failed(logger, $"{source} did not answer with a provisioning manifest (content type {response.Content.Headers.ContentType?.MediaType ?? "none"}).");
            }

            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException)
        {
            return _Failed(logger, $"The provisioning manifest could not be fetched from {source}: {_Scrub(exception.Message, uri, token)}");
        }

        var read = ProvisionManifestReader.Read(json);
        if (read.Error is not null)
        {
            return _Failed(logger, $"{read.Error} Source: {source}.");
        }

        foreach (var note in read.Skipped)
        {
            logger.LogWarning("Provisioning from {Source}: {Path} was skipped ({Reason}): {Detail}", source, note.Path, note.Reason, note.Detail);
        }

        // AC-1506: not used up here. The applier does that once everything was applied, so a failed apply leaves the link usable.
        logger.LogInformation("Provisioning manifest {ManifestId} revision {Revision} from {Source} is valid.", read.Manifest!.ManifestId, read.Manifest.Revision, source);
        return new(read.Manifest, read.Skipped, null, tokenHash is null ? null : new ProvisionToken(token!, tokenHash)) { Source = source };
    }

    // Records the token's hash, so the same link is refused from now on. Server-side it is one-time anyway (DEP-264).
    public static void UseUpToken(string stateRoot, ProvisionToken token)
    {
        var usedTokensPath = Path.Combine(stateRoot, UsedTokensFile);
        var usedTokens = File.Exists(usedTokensPath) ? File.ReadAllLines(usedTokensPath) : [];
        if (!usedTokens.Contains(token.Hash))
        {
            CockpitConfigPath.WriteAllTextPrivate(usedTokensPath, string.Join('\n', usedTokens.Append(token.Hash)) + "\n");
        }
    }

    // https, or http to this machine for a test server (manifest-v1 §5.2).
    private static bool _Trusted(Uri uri) => uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    // A network error may quote the address it tried; only the redacted form of it leaves this method.
    private static string _Scrub(string message, Uri uri, string? token)
    {
        foreach (var secret in new[] { uri.Query, uri.UserInfo, token, Uri.EscapeDataString(token ?? string.Empty) })
        {
            if (!string.IsNullOrEmpty(secret))
            {
                message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private static ProvisioningOutcome _Failed(ILogger logger, string failure)
    {
        logger.LogError("{Failure}", failure);
        return new(null, [], failure);
    }
}

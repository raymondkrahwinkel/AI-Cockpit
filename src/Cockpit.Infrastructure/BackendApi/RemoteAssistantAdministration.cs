using System.Net;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Core.Assistant;
using Cockpit.Core.Profiles;

namespace Cockpit.Infrastructure.BackendApi;

// AC-1475: a server's own assistant over its backend API. A change goes out as the fields it names; the server keeps
// the rest, secrets and the consent bypass included.
internal sealed class RemoteAssistantAdministration(BackendApiClient client) : IAssistantAdministration
{
    private const string Route = "api/v1/assistant/settings";

    public Task<RemoteAssistantSettings> GetAsync(CancellationToken cancellationToken = default) =>
        _CallAsync(HttpMethod.Get, Route, null, cancellationToken);

    public Task<RemoteAssistantSettings> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        _CallAsync(HttpMethod.Put, $"{Route}/enabled", new { enabled }, cancellationToken);

    public Task<RemoteAssistantSettings> UpdateProfileAsync(RemoteAssistantProfilePatch patch, CancellationToken cancellationToken = default) =>
        _CallAsync(HttpMethod.Patch, $"{Route}/profile", patch, cancellationToken);

    // AC-1995: the shared editor's whole profile, masked secrets and all; the server puts each one back.
    public Task<RemoteAssistantSettings> SaveProfileAsync(SessionProfile profile, bool replacesStandingInstruction, CancellationToken cancellationToken = default) =>
        UpdateProfileAsync(new RemoteAssistantProfilePatch
        {
            ReplacesStandingInstruction = replacesStandingInstruction,
            Profile = new RemoteProfilePatch { Profile = RemoteProfileWire.ToJson(profile) },
        }, cancellationToken);

    public async Task<RemoteAssistantSettings?> CopyProfileFromAsync(string label, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _CallAsync(HttpMethod.Post, $"{Route}/profile/copy-from/{Uri.EscapeDataString(label)}", null, cancellationToken).ConfigureAwait(false);
        }
        catch (BackendApiException exception) when (exception.Status == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // A refusal of what was sent carries the server's reason; the door's one answer says the key may no longer reach this.
    private async Task<RemoteAssistantSettings> _CallAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await client.SendAsync<RemoteAssistantSettings>(method, path, body, ConnectKeyEndpoints.Json, cancellationToken).ConfigureAwait(false);
            return settings.Profile is { } profile ? settings with { Profile = RemoteServerProfiles.Read(profile) } : settings;
        }
        catch (BackendApiException exception) when (exception.Status is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            throw new ArgumentException(exception.Description, exception);
        }
        catch (BackendApiException exception) when (exception.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(exception.Description, exception);
        }
    }
}

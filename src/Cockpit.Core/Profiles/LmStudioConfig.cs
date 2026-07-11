namespace Cockpit.Core.Profiles;

/// <summary>Connection settings for an LM Studio profile: its OpenAI-compatible server, the model, and an optional API key (only needed behind a key-protected proxy).</summary>
/// <param name="BaseUrl">Server base URL, e.g. <c>http://localhost:1234</c>.</param>
/// <param name="Model">Model id as reported by <c>/v1/models</c>.</param>
/// <param name="ApiKey">Bearer key when the server is key-protected; <see langword="null"/> for the usual local setup.</param>
/// <param name="SystemPrompt">Optional base system prompt sent as the first (system) message of every conversation for this profile.</param>
public sealed record LmStudioConfig(string BaseUrl, string Model, string? ApiKey = null, string? SystemPrompt = null) : ProviderConfig(SessionProvider.LmStudio)
{
    /// <summary>
    /// Overrides the record's auto-generated <c>ToString()</c>, which would otherwise print <see cref="ApiKey"/>
    /// in the clear — anywhere this config lands in a log line or exception message (a leak surface, not just
    /// a display concern; DPAPI-at-rest for the stored value is a separate, later decision).
    /// </summary>
    public override string ToString() =>
        $"{nameof(LmStudioConfig)} {{ BaseUrl = {BaseUrl}, Model = {Model}, ApiKey = {(string.IsNullOrEmpty(ApiKey) ? "null" : "***")}, SystemPrompt = {SystemPrompt} }}";
}

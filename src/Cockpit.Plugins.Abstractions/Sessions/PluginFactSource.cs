namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// Where one fact in a <see cref="PluginModelIntelligence"/> came from and how long it holds (AC-1808), so whoever
/// reads it can always say how old it is and whose word it is. A provider reports only what it read live or from a
/// public source; a figure it compiled in has no source to name and does not belong in the reading.
/// </summary>
/// <param name="Name">
/// What the fact was read from, in words a person can follow back, e.g. <c>"codex app-server account/rateLimits/read"</c>
/// or <c>"https://openrouter.ai/api/v1/models"</c>.
/// </param>
/// <param name="AsOf">
/// When the provider read it — not when the host asked.
/// </param>
/// <param name="ValidFor">
/// How long after <paramref name="AsOf"/> the provider vouches for it; past that, a consumer shows it as stale.
/// </param>
public sealed record PluginFactSource(string Name, DateTimeOffset AsOf, TimeSpan ValidFor);

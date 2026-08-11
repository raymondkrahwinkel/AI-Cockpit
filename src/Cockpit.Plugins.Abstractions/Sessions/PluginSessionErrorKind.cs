namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>What kind of failure a <see cref="PluginSessionError"/> reports, so the host can render it without
/// guessing from the message text. AC-720: additive, defaults to <see cref="Unknown"/> — a plugin compiled
/// before this field existed still constructs a valid <see cref="PluginSessionError"/>.</summary>
public enum PluginSessionErrorKind
{
    Unknown,
    AuthRequired,
    RateLimited,
    ServiceUnavailable,
}

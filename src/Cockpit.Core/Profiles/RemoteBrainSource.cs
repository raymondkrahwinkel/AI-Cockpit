namespace Cockpit.Core.Profiles;

// AC-1995: a memory source on a server that lists brains, as the brain picker reads it over the admin connection.
// `Error` is the server's own sentence when the source did not list, never the source's; null when `Brains` is its list.
public sealed record RemoteBrainSource(string Scheme, string Title, string? InstanceTitle, IReadOnlyList<RemoteBrainSource.Brain> Brains, string? Error)
{
    // One brain: `Value` is what a profile stores after the scheme, `Detail` what tells two same-named brains apart.
    public sealed record Brain(string Value, string Name, string? Detail);
}

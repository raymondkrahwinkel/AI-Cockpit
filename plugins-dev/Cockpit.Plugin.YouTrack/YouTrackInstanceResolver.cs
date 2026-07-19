namespace Cockpit.Plugin.YouTrack;

/// <summary>Picks the YouTrack instance an attach targets (AC-116).</summary>
internal static class YouTrackInstanceResolver
{
    /// <summary>
    /// The instance to attach to: matched by the issue URL's <paramref name="host"/> when the tool result
    /// carried one, else — only when no host is known — the sole configured instance (the common
    /// single-YouTrack setup). Returns null when a known host matches none, or when several instances are
    /// configured and there is no host to tell them apart, so an image is never attached to the wrong YouTrack.
    /// Only instances with both a URL and a token count (one still being filled in cannot be a target). The
    /// host only ever <em>selects</em> among configured instances — the request always goes to the matched
    /// instance's own URL and token, never to a host taken from the (agent-influenced) result.
    /// </summary>
    public static YouTrackInstance? Resolve(IReadOnlyList<YouTrackInstance> instances, string? host)
    {
        var configured = instances
            .Where(instance => !string.IsNullOrWhiteSpace(instance.InstanceUrl) && !string.IsNullOrWhiteSpace(instance.Token))
            .ToList();

        if (configured.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(host))
        {
            // A host was named: attach only to the instance it names, never a fallback — the issue lives on a
            // specific YouTrack, and guessing a different one would put the image in the wrong place.
            return configured.FirstOrDefault(instance =>
                string.Equals(YouTrackUrl.HostOf(instance.InstanceUrl), host, StringComparison.OrdinalIgnoreCase));
        }

        return configured.Count == 1 ? configured[0] : null;
    }
}

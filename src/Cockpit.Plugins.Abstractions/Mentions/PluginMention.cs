namespace Cockpit.Plugins.Abstractions.Mentions;

/// <summary>
/// One suggestion an <see cref="IPluginMentionSource"/> offers. Choosing it is always a mention, never the message's project.
/// </summary>
/// <param name="Title">The row's title.</param>
/// <param name="SourceLine">Optional second line under the title, before "via <c>SourceName</c>".</param>
/// <param name="LastActivity">Optional moment this item was last active, for ordering.</param>
/// <param name="InsertText">What follows the '@' in the message once chosen.</param>
/// <param name="Reference">What the prompt carries for this mention, such as a URL or a "<c>scheme:value</c>" id.</param>
public sealed record PluginMention(string Title, string? SourceLine, DateTimeOffset? LastActivity, string InsertText, string Reference);

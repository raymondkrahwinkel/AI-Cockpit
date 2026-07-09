using System.Text;

namespace Cockpit.Core.Plugins;

/// <summary>
/// Turns a plugin id into a filesystem-safe folder slug: lowercase, <c>[a-z0-9-]</c>, other runs
/// collapsed to a single dash, trimmed. Returns empty when nothing usable remains — the caller then
/// falls back to a generated installation id (a GUID) so every plugin still gets a unique folder.
/// </summary>
public static class PluginFolderName
{
    public static string Normalize(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(id.Length);
        var lastWasDash = false;
        foreach (var character in id.Trim().ToLowerInvariant())
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(character);
                lastWasDash = false;
            }
            else if (!lastWasDash && builder.Length > 0)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }
}

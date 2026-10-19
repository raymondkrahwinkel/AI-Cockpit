using System.Text.Json;
using Cockpit.Core.Backup;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Backup;

// AC-1503: the restart in between. A restore ends in a restart, so the sign-ins it left empty wait in a file in the state
// root until the next start takes them. Labels and plugin ids only, never a value, and gone once read.
public static class RestoredSignIns
{
    private const string FileName = "restored-signins.json";

    internal static void Save(string root, IReadOnlyList<EmptiedSignIn> signIns)
    {
        var file = Path.Combine(root, FileName);

        // The last restore has the last word: a file an earlier one left behind must not outlive a restore with nothing to ask.
        if (signIns.Count == 0)
        {
            return;
        }

        CockpitConfigPath.WriteAllTextPrivate(file, JsonSerializer.Serialize(signIns));
    }

    public static IReadOnlyList<EmptiedSignIn> Take(string root)
    {
        var file = Path.Combine(root, FileName);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<EmptiedSignIn>>(File.ReadAllText(file)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            File.Delete(file);
        }
    }
}

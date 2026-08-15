using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// AC-783: one `session/update` notification's mapping result — events plus a fresh `configOptions` snapshot
// for a `config_option_update`, which carries no transcript event of its own. Mirrors KimiSessionUpdateMapResult.
internal sealed record OpencodeSessionUpdateMapResult(IReadOnlyList<PluginSessionEvent> Events, JsonElement? ConfigOptions)
{
    public static readonly OpencodeSessionUpdateMapResult Empty = new([], null);
}

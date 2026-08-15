using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.OpencodeProvider;

// One `session/update` notification's mapping result (AC-783): the zero-or-more `PluginSessionEvent`s it
// produced, plus a fresh `configOptions` snapshot when the update was a `config_option_update` — that
// variant carries no transcript event of its own (there is no `PluginSessionEvent` for "the option list
// changed"), so `OpencodeSessionUpdateMapper` routes it out here instead, for the driver to rebuild
// `LiveOptions` from. Mirrors Cockpit.Plugin.KimiProvider.KimiSessionUpdateMapResult. `usage_update` is
// handled separately by the driver, not through this mapper — see OpencodeAcpSessionDriver's remarks.
internal sealed record OpencodeSessionUpdateMapResult(IReadOnlyList<PluginSessionEvent> Events, JsonElement? ConfigOptions)
{
    public static readonly OpencodeSessionUpdateMapResult Empty = new([], null);
}

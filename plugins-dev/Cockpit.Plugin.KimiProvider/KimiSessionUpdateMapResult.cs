using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

// One `session/update` notification's mapping result (AC-270 sub [c]): the zero-or-more
// `PluginSessionEvent`s it produced, plus a fresh `configOptions` snapshot when the update was
// a `config_option_update` — that variant carries no transcript event of its own (there is no
// `PluginSessionEvent` for "the option list changed"), so `KimiSessionUpdateMapper`
// routes it out here instead, for the driver to rebuild `LiveOptions` from (AC-272 sub [e]).
internal sealed record KimiSessionUpdateMapResult(IReadOnlyList<PluginSessionEvent> Events, JsonElement? ConfigOptions)
{
    public static readonly KimiSessionUpdateMapResult Empty = new([], null);
}

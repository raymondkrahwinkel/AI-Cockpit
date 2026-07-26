using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// One <c>session/update</c> notification's mapping result (AC-270 sub [c]): the zero-or-more
/// <see cref="PluginSessionEvent"/>s it produced, plus a fresh <c>configOptions</c> snapshot when the update was
/// a <c>config_option_update</c> — that variant carries no transcript event of its own (there is no
/// <see cref="PluginSessionEvent"/> for "the option list changed"), so <see cref="KimiSessionUpdateMapper"/>
/// routes it out here instead, for the driver to rebuild <c>LiveOptions</c> from (AC-272 sub [e]).
/// </summary>
internal sealed record KimiSessionUpdateMapResult(IReadOnlyList<PluginSessionEvent> Events, JsonElement? ConfigOptions)
{
    public static readonly KimiSessionUpdateMapResult Empty = new([], null);
}

using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// One outstanding permission request: the id to echo back plus the offered `options`, kept as one immutable
// entry (not two dictionaries) so a duplicate toolCallId can't drift the rendered card from the answer.
// Mirrors KimiPendingApproval; options shape measured live to match Kimi's three kinds.
internal sealed record OpencodePendingApproval(JsonElement RequestId, JsonElement Options);

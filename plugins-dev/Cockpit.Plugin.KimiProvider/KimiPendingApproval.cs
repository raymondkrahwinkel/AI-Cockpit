using System.Text.Json;

namespace Cockpit.Plugin.KimiProvider;

/// <summary>
/// One outstanding <c>session/request_permission</c> reverse-request (P0-5): the JSON-RPC id to echo back in
/// the answer, bundled with the exact <c>options</c> array that was shown to the operator for this toolCallId.
/// Kept as one immutable entry, keyed by toolCallId, rather than two separate dictionaries — two dictionaries
/// updated independently for the same key can drift apart on a duplicate id (the id overwritten but the old
/// options left behind, or vice versa), which is exactly the confused-deputy risk this record removes: "what
/// was rendered to the operator" and "what gets answered" can no longer come from different requests.
/// </summary>
/// <param name="RequestId">The request id, cloned verbatim, so it can be echoed back in the response.</param>
/// <param name="Options">The request's offered <c>options</c> array, cloned; <see cref="JsonValueKind.Undefined"/> when it carried none.</param>
internal sealed record KimiPendingApproval(JsonElement RequestId, JsonElement Options);

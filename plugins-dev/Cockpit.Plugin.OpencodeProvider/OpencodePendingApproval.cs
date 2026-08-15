using System.Text.Json;

namespace Cockpit.Plugin.OpencodeProvider;

// One outstanding `session/request_permission` reverse-request: the JSON-RPC id to echo back in the
// answer, bundled with the exact `options` array that was shown to the operator for this toolCallId. Kept as
// one immutable entry, keyed by toolCallId, rather than two separate dictionaries — two dictionaries updated
// independently for the same key can drift apart on a duplicate id (the id overwritten but the old options
// left behind, or vice versa), which is exactly the confused-deputy risk this record removes: "what was
// rendered to the operator" and "what gets answered" can no longer come from different requests. Mirrors
// Cockpit.Plugin.KimiProvider.KimiPendingApproval.
//
// `RequestId`: The request id, cloned verbatim, so it can be echoed back in the response.
// `Options`: The request's offered `options` array, cloned — measured live against opencode 1.18.18:
// `[{"optionId":"once","kind":"allow_once","name":"Allow once"},{"optionId":"always","kind":"allow_always","name":"Always allow"},{"optionId":"reject","kind":"reject_once","name":"Reject"}]`,
// the same three kinds Kimi offers. `JsonValueKind.Undefined` when it carried none.
internal sealed record OpencodePendingApproval(JsonElement RequestId, JsonElement Options);

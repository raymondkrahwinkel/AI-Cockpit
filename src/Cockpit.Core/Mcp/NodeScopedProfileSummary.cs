using Cockpit.Core.Profiles;

namespace Cockpit.Core.Mcp;

// AC-794's answer to "what does a scoped controller get told about a profile it may use" — the allow-list
// `SharedProjectPublishDefinition` already sets the precedent for at the Depot boundary, one boundary earlier here.
// A `SessionProfile` carries far more than this: `ProviderConfig` (a provider's own settings, which for several
// providers is where an API key actually lives), `EnvironmentVariables` (a profile-authored name/value pair, no
// guarantee the name is one `SecretFields` would catch), `DefaultWorkingDirectory` (this machine's filesystem
// layout), `SystemPrompt` (free text an operator may have written anything into). None of that is secret-shaped by
// AC-353's naming rule, so none of it is caught by "strip what looks like a secret" — which is exactly the gap
// criterion 5 names: a field has to be deliberately let through, not merely fail to look dangerous.
//
// So this carries only what a controller needs to let its operator recognise and pick a profile — nothing a
// session under it would need to actually run one; that is `[f]`'s problem, and it must not reach for a `Profile`
// field this type does not offer without adding it here first, in the open, with the secrecy test below updated.
public sealed record NodeScopedProfileSummary(string Label, SessionProvider Provider, string? Purpose);

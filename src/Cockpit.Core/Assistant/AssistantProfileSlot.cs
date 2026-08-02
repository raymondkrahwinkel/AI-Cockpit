using Cockpit.Core.Profiles;

namespace Cockpit.Core.Assistant;

/// <summary>
/// The Assistant Profile: a <em>slot</em> with a replaceable <see cref="SessionProfile"/> record behind it (AC-543).
/// </summary>
/// <remarks>
/// A <see cref="SessionProfile"/> cannot change provider — "a different provider means a new profile, so credentials
/// and configuration never end up describing a backend the profile no longer talks to". But the assistant must be
/// switchable between Claude, Codex and a local model. Two layers resolve that without weakening the invariant:
/// <list type="bullet">
///   <item>the <b>slot</b> — this type — has a stable id (<see cref="SlotId"/>), a fixed display name
///     (<see cref="DisplayName"/>), and cannot be deleted;</item>
///   <item>the <b>record</b> — <see cref="Profile"/> — is an ordinary profile with a fixed provider. Switching
///     provider mints a <em>new</em> record and repoints the slot at it; the old record is dropped afterwards.</item>
/// </list>
/// <para>
/// <b>The invariant, stated so it can be tested:</b> no <see cref="SessionProfile"/> record ever changes provider.
/// A <c>with { ProviderConfig = … }</c> anywhere on the assistant path is the bug this design exists to prevent —
/// it compiles, it appears to work, and it is exactly what the record's own doc-comment forbids.
/// </para>
/// <para>
/// <b>Found by id, never by label.</b> The slot lives in its own <c>assistantProfile</c> section of
/// <c>cockpit.json</c> rather than as an entry in the profile list, so there is nothing to match a label against
/// and AC-410's rename-reads-as-gone bug cannot reach it. That placement is also why the slot is not deletable,
/// why it does not appear in <em>+ New session</em>, and why <c>list_profiles</c> never offers it as a delegation
/// target: it is not in the list those three read. Guards would have had to be added, remembered, and kept — this
/// is the same property by construction.
/// </para>
/// </remarks>
/// <param name="Profile">
/// The record the slot currently points at, or <see langword="null"/> when the slot is deliberately unset —
/// a fresh install, or a provider switch that failed and landed here with <paramref name="UnsetReason"/> filled in.
/// </param>
/// <param name="UnsetReason">
/// Why <paramref name="Profile"/> is <see langword="null"/>, in words for the operator. Never null while
/// <paramref name="Profile"/> is set. An empty slot with no reason is the failure mode criterion 4 rules out:
/// the operator is owed either the old profile or an explanation, never a blank row.
/// </param>
/// <param name="ReplacesStandingInstruction">
/// Whether the record's own system prompt <em>replaces</em> <see cref="AssistantSystemPrompt.Default"/> instead of
/// being added to it (AC-594). Default false: what an operator types is an addition.
/// </param>
public sealed record AssistantProfileSlot(
    SessionProfile? Profile = null,
    string? UnsetReason = null,
    bool ReplacesStandingInstruction = false)
{
    /// <summary>
    /// The slot's stable identity. A constant rather than a generated id: there is exactly one slot, it is created
    /// by the app rather than by the operator, and a value that could differ per machine would be one more thing
    /// that can go missing.
    /// </summary>
    public const string SlotId = "cockpit_assistant_profile";

    /// <summary>
    /// What the operator sees the slot called, fixed regardless of what the record behind it is labelled. The
    /// record's own <see cref="SessionProfile.Label"/> is free to say "Claude (assistant)" or anything else and
    /// may be renamed at will — nothing resolves the slot through it.
    /// </summary>
    public const string DisplayName = "Assistant Profile";

    /// <summary>True when the assistant has a usable profile to run under. <see langword="false"/> means <see cref="UnsetReason"/> says why.</summary>
    public bool IsConfigured => Profile is not null;

    /// <summary>
    /// The empty slot, with the reason the operator is owed. The only way to build one without a profile — the
    /// default constructor would otherwise permit <c>new AssistantProfileSlot()</c>, which is precisely the blank
    /// row with no explanation that criterion 4 rules out, reachable by forgetting an argument.
    /// </summary>
    public static AssistantProfileSlot Unset(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? throw new ArgumentException("An unset Assistant Profile must say why it is unset.", nameof(reason))
            : new AssistantProfileSlot(null, reason);
}

using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// Wraps a real <c>SessionStateStore</c> so a test can inject timing or a read failure into one specific call
/// without losing the real store's own semantics (last-record-wins, atomic append) — the same reason
/// <see cref="SessionStateRecorderTests"/> otherwise avoids a full substitute for <see cref="ISessionStateStore"/>.
/// Every hook is optional and defaults to a plain passthrough to <paramref name="inner"/>.
/// </summary>
internal sealed class InstrumentedSessionStateStore(ISessionStateStore inner) : ISessionStateStore
{
    /// <summary>Replaces <see cref="TryLoadAsync"/>'s result entirely when set, instead of forwarding to <paramref name="inner"/> — used to inject a read failure without needing an actually-unreadable file.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<SessionStateRecord>?>>? TryLoadOverride { get; set; }

    /// <summary>Awaited, with the record about to be written, before a <see cref="RecordAsync"/> call reaches <paramref name="inner"/> — used to hold one specific write back so a test can force an append ordering.</summary>
    public Func<SessionStateRecord, CancellationToken, Task>? BeforeRecord { get; set; }

    public async Task RecordAsync(SessionStateRecord record, CancellationToken cancellationToken = default)
    {
        if (BeforeRecord is not null)
        {
            await BeforeRecord(record, cancellationToken).ConfigureAwait(false);
        }

        await inner.RecordAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<SessionStateRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        inner.LoadAsync(cancellationToken);

    public Task<IReadOnlyList<SessionStateRecord>?> TryLoadAsync(CancellationToken cancellationToken = default) =>
        TryLoadOverride is not null ? TryLoadOverride(cancellationToken) : inner.TryLoadAsync(cancellationToken);

    public Task CompactAsync(IReadOnlySet<string>? knownPaneIds = null, CancellationToken cancellationToken = default) =>
        inner.CompactAsync(knownPaneIds, cancellationToken);
}

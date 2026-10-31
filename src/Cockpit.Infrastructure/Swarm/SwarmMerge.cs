using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Swarm;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.Assistant;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Swarm;

internal sealed record SwarmMergeInput(string Name, string WinnerName, SwarmStandbySnapshot Snapshot);

// AC-1929: two leaders after a split. The one stepping down parks its own copy in an outbox until the leader took it;
// the leader parks it in an inbox and restarts the assistant, whose start reads the copy through `Apply`. Both boxes
// are files, so a crash in between loses nothing.
internal sealed class SwarmMerge(IServiceProvider services, ISwarmLeadership leadership) : ISingletonService
{
    internal const string NoteStateField = "note_state";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _files = new();

    private string Root => services.GetRequiredService<SwarmStandbyCopy>().Folder;
    private string Outbox => Path.Combine(Root, "swarm-merge-outbox.json");
    private string KeptPath => Path.Combine(Root, "swarm-merge-kept.json");
    private string Inbox(SwarmMemberId from) => Path.Combine(Root, $"swarm-merge-inbox-{from.Value}.json");

    internal void Park(SwarmStandbySnapshot own)
    {
        lock (_files)
        {
            CockpitConfigPath.ReplaceAtomicallyPrivate(Outbox, stream => JsonSerializer.Serialize(stream, own, SwarmStandbyCopy.Json));
        }
        (leadership as SwarmLeadership)?.SetMerging(true);
    }

    internal async Task FlushAsync(CancellationToken ct)
    {
        var parked = _ReadOutbox();
        if (parked is null || leadership.Current is not { } leader)
        {
            return;
        }

        try
        {
            var delivered = leadership.IsSelf
                ? await ReceiveAsync(parked, ct)
                : await services.GetRequiredService<SwarmTakeoverCoordinator>().PostAsync(leader, "swarm/merge", parked, ct);
            if (!delivered)
            {
                return;
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // The outbox stays; the next heartbeat tries again.
            services.GetRequiredService<ILogger<SwarmMerge>>().LogWarning(ex, "The merge copy did not reach {Member} yet.", leader.Name);
            return;
        }

        lock (_files)
        {
            // A copy parked again while this one travelled stays for the next round.
            var now = _ReadOutbox();
            if (now?.Generation != parked.Generation || now.Revision != parked.Revision)
            {
                return;
            }
            File.Delete(Outbox);
        }
        (leadership as SwarmLeadership)?.SetMerging(false);
    }

    internal async Task<bool> ReceiveAsync(SwarmStandbySnapshot loser, CancellationToken ct)
    {
        if (!leadership.IsSelf || !SwarmMemberId.TryParse(loser.LeaderId, out var from))
        {
            return false;
        }

        await _gate.WaitAsync(ct);
        try
        {
            var members = (await services.GetRequiredService<SwarmMembershipStore>().LoadAsync(ct)).Members.ToArray();
            var self = services.GetRequiredService<SwarmIdentity>().MemberId;
            var input = new SwarmMergeInput(members.FirstOrDefault(member => member.Id == from)?.Name ?? from.Value,
                members.FirstOrDefault(member => member.Id == self)?.Name ?? self.Value, loser);
            var own = await services.GetRequiredService<SwarmTakeoverCoordinator>().CaptureAsync(ct)
                ?? services.GetRequiredService<SwarmStandbyCopy>().Read();
            if (own is not null && !_Merge(own, input).Changed)
            {
                return true;
            }

            lock (_files)
            {
                CockpitConfigPath.ReplaceAtomicallyPrivate(Inbox(from), stream => JsonSerializer.Serialize(stream, input, SwarmStandbyCopy.Json));
            }
            (leadership as SwarmLeadership)?.SetMerging(true);
            var host = services.GetRequiredService<AssistantSessionHost>();
            if (services.GetService<IAssistantSessionFactory>() is { } factory)
            {
                await factory.OnOwnerThreadAsync(() =>
                {
                    host.RequestMergeRestart();
                    return Task.FromResult(true);
                });
            }
            else
            {
                host.RequestMergeRestart();
            }
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Runs on the assistant's start as the leader: folds every parked copy into `standby` and saves the result.
    internal SwarmStandbySnapshot? Apply(SwarmStandbySnapshot? standby)
    {
        lock (_files)
        {
            var self = services.GetRequiredService<SwarmIdentity>().MemberId.Value;
            var inbox = Directory.Exists(Root) ? Directory.GetFiles(Root, "swarm-merge-inbox-*.json") : [];
            foreach (var path in inbox)
            {
                var input = JsonSerializer.Deserialize<SwarmMergeInput>(File.ReadAllText(path), SwarmStandbyCopy.Json)
                    ?? throw new InvalidDataException("Empty swarm merge input.");
                var own = standby ?? input.Snapshot with
                {
                    LeaderId = self, Transcript = [], NoteState = "", NoteStateAt = default, Behaviour = "", Receipts = [], PendingInbox = [],
                };
                var (merged, result, _) = _Merge(own, input);
                standby = merged with { LeaderId = self, Generation = Guid.NewGuid(), Revision = 1, At = DateTimeOffset.UtcNow };
                services.GetRequiredService<SwarmStandbyCopy>().Save(standby);
                CockpitConfigPath.ReplaceAtomicallyPrivate(KeptPath, stream => JsonSerializer.Serialize(stream, result.Kept, SwarmStandbyCopy.Json));
                File.Delete(path);
                if (result.Asks)
                {
                    _Ask(input.Name, result.SeparatorId);
                }
            }
            if (inbox.Length > 0)
            {
                (leadership as SwarmLeadership)?.SetMerging(false);
            }
            return standby;
        }
    }

    private SwarmStandbySnapshot? _ReadOutbox()
    {
        lock (_files)
        {
            return File.Exists(Outbox) ? JsonSerializer.Deserialize<SwarmStandbySnapshot>(File.ReadAllText(Outbox), SwarmStandbyCopy.Json) : null;
        }
    }

    private KeptVersion[] _ReadKept()
    {
        lock (_files)
        {
            return File.Exists(KeptPath) ? JsonSerializer.Deserialize<KeptVersion[]>(File.ReadAllText(KeptPath), SwarmStandbyCopy.Json) ?? [] : [];
        }
    }

    private (SwarmStandbySnapshot Snapshot, ConversationMergeResult Result, bool Changed) _Merge(SwarmStandbySnapshot own, SwarmMergeInput input)
    {
        var self = services.GetRequiredService<SwarmIdentity>().MemberId;
        static Dictionary<string, MergeField> Fields(SwarmStandbySnapshot snapshot) =>
            new() { [NoteStateField] = new(snapshot.NoteState, snapshot.NoteStateAt) };
        var result = ConversationMerge.Merge(new(self, input.WinnerName, own.Transcript, Fields(own)),
            new(SwarmMemberId.TryParse(input.Snapshot.LeaderId, out var loser) ? loser : self, input.Name, input.Snapshot.Transcript,
                Fields(input.Snapshot)), _ReadKept());
        var carried = AssistantMemoryFile._Entries(own.Behaviour).Select(AssistantMemoryFile._StripOrigin).ToHashSet(StringComparer.Ordinal);
        var extra = AssistantMemoryFile._Entries(input.Snapshot.Behaviour).Where(line => !carried.Contains(AssistantMemoryFile._StripOrigin(line)))
            .Select(line => "- " + line).ToArray();
        var note = result.Fields[NoteStateField];
        var merged = own with
        {
            Transcript = [.. result.Rows],
            NoteState = note.Value,
            NoteStateAt = note.ChangedAt,
            Behaviour = extra.Length == 0 ? own.Behaviour
                : string.IsNullOrWhiteSpace(own.Behaviour) ? input.Snapshot.Behaviour : own.Behaviour + Environment.NewLine + string.Join(Environment.NewLine, extra),
            Receipts = [.. own.Receipts.Concat(input.Snapshot.Receipts).DistinctBy(receipt => (receipt.Source, receipt.MessageId))],
            PendingInbox = [.. own.PendingInbox.Concat(input.Snapshot.PendingInbox).DistinctBy(message => message.Id)],
        };
        var changed = result.Asks || extra.Length > 0 || result.Rows.Count != own.Transcript.Length || note.Value != own.NoteState
            || merged.Receipts.Length != own.Receipts.Length || merged.PendingInbox.Length != own.PendingInbox.Length;
        return (merged, result, changed);
    }

    // Asked once: on the merge's divider in the assistant chat, or as a notification when no divider was written.
    private void _Ask(string name, string? dividerId)
    {
        var text = $"The note state changed on this machine and on {name} while they were apart. The newest version stays; the other is kept next to it.";
        if (dividerId is not null && leadership is SwarmLeadership own)
        {
            own.SetMergeQuestion(new(dividerId, text, _Compare, () =>
            {
                _KeepNewest();
                own.SetMergeQuestion(null);
            }));
            return;
        }

        services.GetService<IToastService>()?.Show(text, ToastSeverity.Information,
            [new ToastAction("Compare", _Compare), new ToastAction("Keep the newest", _KeepNewest)],
            true, "Swarm", "swarm-merge", "2 leaders seen · conversation merged");
    }

    private void _Compare()
    {
        try
        {
            var text = new StringBuilder("# Newest (in use)").AppendLine().AppendLine()
                .AppendLine(services.GetRequiredService<SwarmStandbyCopy>().Read()?.NoteState);
            foreach (var kept in _ReadKept())
            {
                text.AppendLine().AppendLine($"# Kept from {kept.Origin}").AppendLine().AppendLine(kept.Version.Value);
            }
            var path = Path.Combine(Root, "swarm-merge-compare.md");
            CockpitConfigPath.ReplaceAtomicallyPrivate(path, text.ToString());
            using var _ = Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            services.GetRequiredService<ILogger<SwarmMerge>>().LogWarning(ex, "Could not open the merged note state for comparison.");
        }
    }

    private void _KeepNewest()
    {
        lock (_files)
        {
            File.Delete(KeptPath);
        }
    }
}

using System.Text.Json;
using Cockpit.Plugins.Abstractions.Files;

namespace Cockpit.Plugin.Depot.Notes.Live;

internal sealed partial class DepotNoteSession
{
    private ExternalWriter? _externalWriter;
    private bool _ambiguousExternalWriter;
    private bool _outsidePending;
    private DateTimeOffset _outsideAt;

    private async Task _OutsideAsync(JsonElement payload)
    {
        var writer = payload.Deserialize<ExternalWriter>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ambiguousExternalWriter |= _outsidePending;
        _outsidePending = true;
        _outsideAt = DateTimeOffset.UtcNow;
        _externalWriter = writer is { Source: "agent", Name.Length: > 0 } ? writer : null;
        if (_externalWriter is { Via: "Mcp", Version: > 0 } saved)
        {
            _SetSaved(new LiveFileSave(saved.Name, saved.Color ?? DepotNotePresence.OutsideAgentColor, _time.GetUtcNow()));
        }
        if (_externalWriter is { } agent && _presence is { } presence)
        {
            await presence.OutsideAsync(agent.Label, _life.Token, agent.Color).ConfigureAwait(false);
        }
    }

    private async Task _MergedAsync(int[] blocks)
    {
        await _text.WaitAsync(_life.Token).ConfigureAwait(false);
        try
        {
            var writer = _externalWriter;
            _externalWriter = null;
            _outsidePending = false;
            var ambiguous = _ambiguousExternalWriter;
            // Merges can be coalesced: without correlation, ambiguity ends only at the next Join.
            if (writer is null || ambiguous || !_loaded)
            {
                return;
            }
            var ranges = await _room.BlockRangesAsync(blocks, _life.Token).ConfigureAwait(false);
            if (ranges.Count > 0)
            {
                _rev++;
                Patched?.Invoke(this, new LiveFilePatch(_rev, [])
                {
                    ExternalEdit = new ExternalTextEdit(writer.Label, _outsideAt, ranges),
                });
            }
        }
        finally
        {
            _text.Release();
        }
    }

    private sealed record ExternalWriter(string Name, string? OnBehalfOf, string Source, string? Color = null, string? Via = null, int? Version = null)
    {
        public string Label => string.IsNullOrWhiteSpace(OnBehalfOf) ? Name : $"{Name} · on behalf of {OnBehalfOf}";
    }
}

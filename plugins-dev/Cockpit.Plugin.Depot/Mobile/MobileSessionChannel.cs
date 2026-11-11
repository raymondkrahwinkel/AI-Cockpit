using System.Text.Json;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Mobile;

// AC-2257: a paired phone's view onto the other sessions of this Cockpit (PROTOCOL.md § 11). It only answers what the
// phone asks, so it keeps nothing per session; it lives and dies with the phone's MobileAssistantChannel, which hands it
// only frames that came through the paired phone's handshake.
internal sealed class MobileSessionChannel(ICockpitHost host, Action<string, Action<Utf8JsonWriter>> send, ILogger? logger)
{
    public const int TranscriptLimit = 200;

    // The assistant has its own channel, one conversation (AC-1092); from here it is a pane that does not exist.
    private const string _AssistantPaneId = "cockpit-assistant";

    private const int _EnvelopeBytes = 1024;
    private const int _PendingSummaryChars = 120;

    public void List(string? id)
    {
        var sessions = _Sessions();
        send("sessions.list", writer =>
        {
            writer.WriteString("replyTo", id);
            writer.WriteBoolean("history", true);
            writer.WriteStartArray("sessions");
            foreach (var session in sessions)
            {
                writer.WriteStartObject();
                writer.WriteString("paneId", session.PaneId);
                writer.WriteString("name", session.Name);
                writer.WriteString("statusline", session.Statusline);
                writer.WriteString("state", _State(session.Status));
                writer.WriteBoolean("terminal", session.IsTerminal);
                writer.WriteBoolean("readable", session.HasReadableTranscript);
                writer.WriteBoolean("images", session.AcceptsImages);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        });
    }

    public async Task TranscriptAsync(string? id, string? paneId, int? count, int? since)
    {
        var session = _Find(paneId);
        PluginTranscriptSlice? slice = null;
        IReadOnlyList<PluginPendingPermission> pending = [];
        if (session is { HasReadableTranscript: true })
        {
            try
            {
                slice = await host.Sessions.ReadTranscriptAsync(session.PaneId, since is null ? Math.Clamp(count ?? 50, 1, TranscriptLimit) : TranscriptLimit).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning("Reading a session's transcript for a phone failed: {Reason}", exception.GetType().Name);
            }
        }

        if (session is not null)
        {
            try
            {
                pending = await host.Sessions.ReadPendingPermissionsAsync(session.PaneId).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning("Reading a session's permission questions for a phone failed: {Reason}", exception.GetType().Name);
            }
        }

        var floor = Math.Max(0, since ?? 0);
        var rows = slice is null ? [] : slice.Entries.Where(entry => entry.Index >= floor).ToList();
        var pendingJson = pending.Select(permission => JsonSerializer.SerializeToUtf8Bytes(new { toolName = permission.ToolName, summary = _Summary(permission) })).ToList();

        // The newest rows win: what does not fit in one frame is left out at the old end, and `more` says so.
        var budget = MobileFrameCipher.MaxPlaintextBytes - _EnvelopeBytes - (paneId?.Length ?? 0) - pendingJson.Sum(item => item.Length + 1);
        var encoded = rows.Select(entry => (entry.Index, Json: _Row(entry, budget))).ToList();
        var size = encoded.Sum(row => row.Json.Length + 1);
        var skip = 0;
        while (skip < encoded.Count && size > budget)
        {
            size -= encoded[skip++].Json.Length + 1;
        }

        var sent = encoded.Skip(skip).ToList();
        send("session.transcript", writer =>
        {
            writer.WriteString("replyTo", id);
            writer.WriteString("paneId", paneId);
            writer.WriteNumber("total", slice?.TotalEntries ?? 0);
            writer.WriteBoolean("readable", slice is not null);
            writer.WriteBoolean("more", sent.Count > 0 ? sent[0].Index > floor : skip > 0);
            writer.WriteStartArray("rows");
            foreach (var row in sent)
            {
                writer.WriteRawValue(row.Json, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("pending");
            foreach (var item in pendingJson)
            {
                writer.WriteRawValue(item, skipInputValidation: true);
            }

            writer.WriteEndArray();
        });
    }

    public async Task HistoryAsync(string? id, string? paneId, int? before, int? count)
    {
        const int pageLimit = 50;
        int limit = Math.Clamp(count ?? pageLimit, 1, pageLimit);
        PluginTranscriptSlice? slice = null;
        string? error = null;
        OpenCockpitSession? session = _Find(paneId);
        if (before is not >= 0)
        {
            error = "A non-negative before row index is required.";
        }
        else if (session is { HasReadableTranscript: true })
        {
            try
            {
                slice = await host.Sessions.ReadTranscriptBeforeAsync(session.PaneId, before.Value, limit).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning("Reading older transcript rows for a phone failed: {Reason}", exception.GetType().Name);
                error = "Older transcript rows could not be read.";
            }
        }

        int budget = MobileFrameCipher.MaxPlaintextBytes - _EnvelopeBytes - (paneId?.Length ?? 0);
        List<(int Index, byte[] Json)> encoded = slice is null ? [] :
            [.. slice.Entries.Where(entry => entry.Index >= 0 && entry.Index < before)
                .Select(entry => (entry.Index, _Row(entry, budget - 1)))];
        int size = encoded.Sum(row => row.Json.Length + 1);
        int skip = 0;
        while (skip < encoded.Count && size > budget)
        {
            size -= encoded[skip++].Json.Length + 1;
        }

        List<(int Index, byte[] Json)> sent = [.. encoded.Skip(skip)];
        // Even a page containing only host markers must advance to the preceding source page.
        int nextBefore = sent.Count > 0 ? sent[0].Index : Math.Max(0, Math.Min(before ?? 0, slice?.TotalEntries ?? 0) - limit);
        send("session.history", writer =>
        {
            writer.WriteString("replyTo", id);
            writer.WriteString("paneId", paneId);
            writer.WriteBoolean("readable", slice is not null);
            writer.WriteString("error", error);
            writer.WriteNumber("nextBefore", nextBefore);
            writer.WriteBoolean("more", slice is not null && nextBefore > 0);
            writer.WriteStartArray("rows");
            foreach ((int _, byte[] json) in sent)
            {
                writer.WriteRawValue(json, skipInputValidation: true);
            }

            writer.WriteEndArray();
        });
    }

    public async Task SendAsync(string? id, string? paneId, string? text, (IReadOnlyList<byte[]> Images, string? Refusal) attached)
    {
        var session = _Find(paneId);
        var (images, imagesRefused) = attached;
        string? error = null;
        var queued = false;
        if (session is null)
        {
            error = "There is no such session on this Cockpit.";
        }
        else if (session.IsTerminal)
        {
            // A shell from the phone is a power nobody asked for; the phone talks to agents.
            error = "Messages go to agent sessions, not to a plain terminal.";
        }
        else if (string.IsNullOrWhiteSpace(text) && images.Count == 0)
        {
            error = "The message is empty.";
        }
        else
        {
            // A busy session takes it after its turn, from the host's own send queue (AC-294). A session that cannot see
            // images still gets the text; the host says why the images stayed behind (AC-2265).
            queued = _State(session.Status) == "working";
            try
            {
                var result = await host.SendToSessionAsync(session.PaneId, text ?? string.Empty, images).ConfigureAwait(false);
                imagesRefused = MobileAssistantChannel.Join(imagesRefused, result.ImagesRefused);
                if (!result.Delivered)
                {
                    error = "The session did not take the message.";
                    queued = false;
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logger?.LogWarning("Handing a phone's message to a session failed: {Reason}", exception.GetType().Name);
                error = "The session did not take the message.";
                queued = false;
            }
        }

        send("session.sent", writer =>
        {
            writer.WriteString("replyTo", id);
            writer.WriteBoolean("ok", error is null);
            writer.WriteBoolean("queued", queued);
            writer.WriteString("error", error);
            if (imagesRefused is not null)
            {
                writer.WriteString("imagesRefused", imagesRefused);
            }
        });
    }

    private List<OpenCockpitSession> _Sessions() =>
        [.. host.Sessions.OpenSessions.Where(session => !string.Equals(session.PaneId, _AssistantPaneId, StringComparison.Ordinal))];

    private OpenCockpitSession? _Find(string? paneId) =>
        string.IsNullOrEmpty(paneId) ? null : _Sessions().FirstOrDefault(session => string.Equals(session.PaneId, paneId, StringComparison.Ordinal));

    // The host's SessionStatus names, folded onto the four the phone shows.
    private static string _State(string? status) => status switch
    {
        "NeedsAttention" => "waiting",
        "Busy" or "WorkingBackground" => "working",
        "Failed" => "failed",
        _ => "idle",
    };

    // The § 10 row shape with the transcript index as its id; a row too big for a frame keeps the start of its text.
    private static byte[] _Row(PluginTranscriptEntry entry, int budget)
    {
        var text = entry.Text;
        var result = entry.ToolResult;
        while (true)
        {
            var encoded = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = entry.Index,
                kind = JsonNamingPolicy.CamelCase.ConvertName(entry.Kind.ToString()),
                text,
                resultText = result,
                truncated = text.Length != entry.Text.Length || result?.Length != entry.ToolResult?.Length,
            });
            if (encoded.Length <= budget || (text.Length == 0 && string.IsNullOrEmpty(result)))
            {
                return encoded;
            }

            text = MobileAssistantChannel.Half(text);
            result = result is null ? null : MobileAssistantChannel.Half(result);
        }
    }

    // The tool and the gist of its input — the command, the path — cut short; the phone only shows it.
    private static string _Summary(PluginPendingPermission permission)
    {
        var input = permission.InputJson;
        try
        {
            using var document = JsonDocument.Parse(permission.InputJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        input = property.Value.GetString() ?? input;
                        break;
                    }
                }
            }
        }
        catch (JsonException)
        {
        }

        var summary = $"{permission.ToolName}: {input.ReplaceLineEndings(" ")}";
        return summary.Length <= _PendingSummaryChars ? summary : MobileAssistantChannel.Cut(summary, _PendingSummaryChars - 1) + "…";
    }
}

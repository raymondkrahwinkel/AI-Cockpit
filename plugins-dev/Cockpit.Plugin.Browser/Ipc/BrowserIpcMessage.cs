using System.Text.Json;

namespace Cockpit.Plugin.Browser.Ipc;

internal enum BrowserIpcKind { Request, Response, Cancel, Rejected }

internal sealed record BrowserIpcMessage(
    int Version, Guid Generation, Guid TabId, string SessionId, long RequestId,
    BrowserIpcKind Kind, long Deadline, string Operation, JsonElement Payload);

internal static class BrowserIpcLimits
{
    internal const int Version = 1;
    internal const int MessageBytes = 1024 * 1024;
    internal const int PendingCalls = 64;
    internal const int QueuedRequests = PendingCalls;
    internal const int Tabs = 64;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan CancelGrace = TimeSpan.FromMilliseconds(500);
    internal static readonly TimeSpan CancelSend = TimeSpan.FromMilliseconds(250);
}

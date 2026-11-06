using System.Text.Json;

namespace Cockpit.Plugin.Browser.Ipc;

internal enum BrowserIpcKind { Request, Response, Cancel }

internal sealed record BrowserIpcMessage(
    int Version, Guid Generation, Guid TabId, string SessionId, long RequestId,
    BrowserIpcKind Kind, long Deadline, string Operation, JsonElement Payload);

internal static class BrowserIpcLimits
{
    internal const int Version = 1;
    internal const int MessageBytes = 1024 * 1024;
    internal const int PendingCalls = 64;
    internal const int QueuedRequests = 8;
    internal const int Tabs = 64;
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
}

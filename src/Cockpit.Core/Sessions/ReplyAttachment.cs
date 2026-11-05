namespace Cockpit.Core.Sessions;

public sealed record ReplyAttachment(string Name, byte[] Content, bool IsImage);

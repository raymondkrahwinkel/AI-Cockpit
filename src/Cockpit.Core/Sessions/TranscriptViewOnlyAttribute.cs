namespace Cockpit.Core.Sessions;

// AC-1438: a row member the live view draws and the transcript store does not keep. The store leaves it out of its
// log (`SessionTranscriptLog`); the backend event stream carries it, because a pane draws its rows from there.
[AttributeUsage(AttributeTargets.Property)]
public sealed class TranscriptViewOnlyAttribute : Attribute;

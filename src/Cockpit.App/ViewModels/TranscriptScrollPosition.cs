namespace Cockpit.App.ViewModels;

// AC-953: where a transcript was scrolled to, in terms a fresh view in another host can act on — the index of
// the topmost visible row (`Index`, into the same list the view binds to, `SessionViewModel.VisibleTranscript`)
// and how far its top sits above the viewport's (`Offset`, zero or negative while it is scrolled past).
//
// Not a pixel offset: a virtualising panel only estimates the extent it has not realised yet, so an offset
// restored into a freshly built view lands wherever that estimate happens to be — and the row height differs
// anyway between a 520px-tall window and a 360px-wide dock panel.
internal readonly record struct TranscriptScrollPosition(int Index, double Offset);

namespace Cockpit.App.ViewModels;

// AC-953 stores the top visible-row index plus offset, not pixels: virtualization and host sizes make pixels unstable.
internal readonly record struct TranscriptScrollPosition(int Index, double Offset);

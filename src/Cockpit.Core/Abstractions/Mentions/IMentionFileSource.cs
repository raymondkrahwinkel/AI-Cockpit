namespace Cockpit.Core.Abstractions.Mentions;

// The AC-740 @-mention picker's file list for a working directory — '/'-separated paths relative to it, with a
// trailing '/' marking a directory. Implemented in Infrastructure (git ls-files, cached); this seam is what lets
// MentionPickerViewModel test its open/close/rank state machine without touching a disk.
public interface IMentionFileSource
{
    Task<IReadOnlyList<string>> GetPathsAsync(string workingDirectory, CancellationToken cancellationToken);
}

namespace Cockpit.Infrastructure.Sessions;

// AC-1378: a TTY session needs a pane with a terminal in it, which only a frontend has. The reason travels in the
// message, the way `WorktreeAdmissionException`'s does, so whoever asked can be told why nothing started.
public sealed class TtyLaunchRefusedException(string profileLabel)
    : InvalidOperationException(
        $"'{profileLabel}' would start as a TTY session, and a TTY session needs a terminal that only a Cockpit window can host. Ask for an SDK session instead.")
{
    public string ProfileLabel { get; } = profileLabel;
}

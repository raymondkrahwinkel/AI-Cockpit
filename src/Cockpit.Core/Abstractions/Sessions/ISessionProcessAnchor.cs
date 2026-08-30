namespace Cockpit.Core.Abstractions.Sessions;

public interface ISessionProcessAnchor
{
    IDisposable? Anchor(int processId);
}

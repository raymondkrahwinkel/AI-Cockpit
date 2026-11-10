namespace Cockpit.Plugins.Abstractions.Notifications;

public interface IOperatorPhoneNotifications
{
    /// <summary>
    /// Replaces the operator transport; its return value counts authenticated streams that accepted the ping for sending.
    /// Disposing an older registration leaves its replacement active.
    /// </summary>
    IDisposable Register(Func<OperatorPhoneNotification, int> send);
}

namespace Cockpit.Plugins.Abstractions.Notifications;

public interface IOperatorPhoneNotifications
{
    /// <summary>
    /// Registers an operator transport; its return value counts authenticated streams that accepted the ping for sending.
    /// </summary>
    IDisposable Register(Func<OperatorPhoneNotification, int> send);
}

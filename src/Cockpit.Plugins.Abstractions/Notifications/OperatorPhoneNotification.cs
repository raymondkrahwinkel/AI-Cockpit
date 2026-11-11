namespace Cockpit.Plugins.Abstractions.Notifications;

public sealed record OperatorPhoneNotification(string SenderId, string Sender, string Title, string Text, string Urgency, string Target);

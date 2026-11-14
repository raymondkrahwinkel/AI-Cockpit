namespace Cockpit.Plugins.Abstractions.Channels;

public sealed record OperatorSessionCatalog(string Capability, string Epoch, IReadOnlyList<OperatorSessionChoice> Desks, IReadOnlyList<OperatorSessionChoice> Profiles);

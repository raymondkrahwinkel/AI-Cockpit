using System.Reflection;

namespace Cockpit.Core.Help;

// One assembly that may carry documentation, and who it belongs to. The app registers itself the same way a
// plugin is registered — there is no separate path for the core's own pages, which is what keeps this a
// documentation system rather than a plugin feature with an exception standing next to it (AC-1033).
public sealed record HelpDocumentSource(HelpOwner Owner, Assembly Assembly);

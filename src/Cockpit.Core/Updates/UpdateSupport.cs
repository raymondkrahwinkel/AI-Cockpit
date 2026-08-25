namespace Cockpit.Core.Updates;

// Whether this copy of the cockpit is able to replace itself (AC-385). Not a detail of the update check but the
// thing that decides what may be offered at all: the same build, run two ways, answers differently.
public enum UpdateSupport
{
    // This copy was installed by the updater and can be replaced by it. Downloading and applying an update is a
    // thing the cockpit may offer.
    Supported,

    // This copy was not installed by the updater — unpacked from the tarball, run from a checkout, handed over by
    // a distribution's package manager, or under a test host. It can still be told that a newer build exists, but
    // it cannot fetch one over itself; a first-class answer, not an edge case.
    NotPackaged,
}

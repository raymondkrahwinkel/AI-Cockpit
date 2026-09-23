using System.Net.Sockets;

namespace Cockpit.Core.Mcp;

// AC-796 criterion 2, reused by AC-1352's connect probe: distinct wording for a refused connection, an untrusted
// certificate and a timeout, classified by unwrapping the real exception shape rather than parsing
// exception.Message. Lives in Core so both NodeSessionsClient and the App's connect dialog read one sentence.
public static class NodeConnectionFailure
{
    // A node on a local network answers in milliseconds or it is not there. Long enough to survive a busy
    // machine, short enough that a button does not appear to hang.
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(10);

    public static string Describe(string nodeName, Exception exception, TimeSpan timeout)
    {
        if (Find<NodeCertificatePinMismatchException>(exception) is not null)
        {
            return $"{nodeName} answered with a certificate this cockpit did not pin. That is not the machine you "
                + "paired with, or it was reinstalled — pair again from Options → Security if that is expected.";
        }

        if (Find<SocketException>(exception) is { SocketErrorCode: SocketError.ConnectionRefused })
        {
            return $"{nodeName} refused the connection — nothing is listening there. It looks stopped, not merely out of reach.";
        }

        if (Find<OperationCanceledException>(exception) is not null)
        {
            return $"{nodeName} did not answer within {timeout.TotalSeconds:0}s. The connection may be down, or the "
                + "node may simply be asleep or busy — there is no way to tell which from here.";
        }

        // Real, but not one of the shapes above — the honest "could not reach" rather than picking the
        // closest-sounding category.
        return $"Could not reach {nodeName}: {exception.Message}";
    }

    // Public so a caller that wants the exception itself (AC-1352: a pin mismatch's own Message already names
    // both fingerprints) can unwrap the same way. Walks the whole InnerException chain — a certificate
    // callback's throw arrives wrapped more than one level deep, so a single `.InnerException` check misses it.
    public static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }
}

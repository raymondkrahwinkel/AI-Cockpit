namespace Cockpit.Plugin.Depot.Secrets;

// Mirrors Cockpit.Core.Secrets.SecretProtectionException (AC-607). One exception for both a wrong key and a
// tampered value — AES-GCM cannot tell them apart, and the message never carries the plaintext or the key.
public sealed class ProjectSecretProtectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

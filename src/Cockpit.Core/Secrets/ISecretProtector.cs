namespace Cockpit.Core.Secrets;

/// <summary>
/// Encrypts and decrypts a single credential value, bound to the field it sits in (<paramref name="path"/>).
/// An interface so the config layer can be handed a real protector, or nothing at all when the operator has not
/// turned encryption on — see <see cref="ISecretKeyHolder"/>.
/// </summary>
public interface ISecretProtector
{
    string Protect(string path, string value);

    string Unprotect(string path, string value);
}

/// <summary>
/// A value could not be decrypted: the wrong password, or a value that was altered. Deliberately one exception
/// for both — AES-GCM cannot tell them apart, and pretending otherwise would be a guess dressed as a diagnosis.
/// </summary>
public sealed class SecretProtectionException(string message, Exception? innerException = null)
    : Exception(message, innerException);

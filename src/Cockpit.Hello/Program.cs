using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation.Metadata;
using Windows.Security.Credentials;
using Windows.UI;

// AC-1879: Cockpit.Hello supported | enroll <name> <hwnd> <challenge> | exists <name> | sign <name> <hwnd> <challenge> | delete <name>.
// One line: "ok" (enroll: "ok tpm|software <sig> <sig>", sign: "ok <sig>"), "no", or the KeyCredentialStatus; the prompt
// is owned by <hwnd>, the cockpit's window, so it opens above it. Signatures only travel over this stdout, in base64.
const string Manager = "Windows.Security.Credentials.KeyCredentialManager";

try
{
    switch (args)
    {
        case ["supported"]:
            var supported = ApiInformation.IsMethodPresent(Manager, "RequestCreateForWindowAsync")
                && await KeyCredentialManager.IsSupportedAsync();
            Console.WriteLine(supported ? "ok" : "no");
            return 0;

        // Create and both signatures on the one key object, in one process: the gesture of the creation authorises the
        // signatures, so the operator confirms once (three separate helper runs prompted three times, measured 31-10).
        case ["enroll", var name, var hwnd, var challenge]:
            var window = _Window(hwnd);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var created = await KeyCredentialManager.RequestCreateForWindowAsync(window, name, KeyCredentialCreationOption.ReplaceExisting);
            var createMs = clock.ElapsedMilliseconds;
            if (created.Status != KeyCredentialStatus.Success)
            {
                Console.WriteLine(created.Status.ToString());
                return 0;
            }

            var data = Convert.FromBase64String(challenge).AsBuffer();
            clock.Restart();
            var first = await created.Credential.RequestSignForWindowAsync(window, data);
            var firstMs = clock.ElapsedMilliseconds;
            clock.Restart();
            var second = first.Status == KeyCredentialStatus.Success ? await created.Credential.RequestSignForWindowAsync(window, data) : first;

            // How long each step took: a step that waited on a gesture shows it, so the log says which ones prompted.
            Console.Error.WriteLine($"timing create={createMs}ms sign1={firstMs}ms sign2={clock.ElapsedMilliseconds}ms");
            if (second.Status != KeyCredentialStatus.Success)
            {
                Console.WriteLine(second.Status.ToString());
                return 0;
            }

            // Without a TPM there is nothing to attest: Windows then holds the key in software.
            var attestation = await created.Credential.GetAttestationAsync();
            var storage = attestation.Status == KeyCredentialAttestationStatus.NotSupported ? "software" : "tpm";
            Console.WriteLine($"ok {storage} {Convert.ToBase64String(first.Result.ToArray())} {Convert.ToBase64String(second.Result.ToArray())}");
            return 0;

        case ["exists", var name]:
            var found = await KeyCredentialManager.OpenAsync(name);
            Console.WriteLine(found.Status == KeyCredentialStatus.Success ? "ok" : found.Status.ToString());
            return 0;

        case ["sign", var name, var hwnd, var challenge]:
            var opened = await KeyCredentialManager.OpenAsync(name);
            if (opened.Status != KeyCredentialStatus.Success)
            {
                Console.WriteLine(opened.Status.ToString());
                return 0;
            }

            var signed = await opened.Credential.RequestSignForWindowAsync(_Window(hwnd), Convert.FromBase64String(challenge).AsBuffer());
            Console.WriteLine(signed.Status == KeyCredentialStatus.Success ? "ok " + Convert.ToBase64String(signed.Result.ToArray()) : signed.Status.ToString());
            return 0;

        case ["delete", var name]:
            try
            {
                await KeyCredentialManager.DeleteAsync(name);
            }
            catch (System.Runtime.InteropServices.COMException exception) when (exception.HResult == unchecked((int)0x8009000D))
            {
                // NTE_BAD_KEYSET: the key is already gone, which is what delete was for.
            }

            Console.WriteLine("ok");
            return 0;

        default:
            Console.Error.WriteLine("usage: Cockpit.Hello supported | enroll <name> <hwnd> <challenge> | exists <name> | sign <name> <hwnd> <challenge> | delete <name>");
            return 2;
    }
}
catch (Exception exception)
{
    // DeleteAsync on a key that is gone, an older Windows build, a remote session: the cockpit falls back to the password.
    Console.WriteLine($"error {exception.GetType().Name} 0x{exception.HResult:X8} {exception.Message.ReplaceLineEndings(" ")}");
    return 1;
}

static WindowId _Window(string hwnd) => new(ulong.Parse(hwnd, System.Globalization.CultureInfo.InvariantCulture));

using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation.Metadata;
using Windows.Security.Credentials;
using Windows.UI;

// AC-1879: Cockpit.Hello supported | create <name> <hwnd> | sign <name> <hwnd> <challenge-base64> | delete <name>.
// Prints one line: "ok" (sign: "ok <signature-base64>"), "no", or the KeyCredentialStatus; the prompt is owned by
// <hwnd>, the cockpit's window, so it opens above it. The signature only ever travels over this process's stdout.
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

        case ["create", var name, var hwnd]:
            var created = await KeyCredentialManager.RequestCreateForWindowAsync(_Window(hwnd), name, KeyCredentialCreationOption.ReplaceExisting);
            Console.WriteLine(created.Status == KeyCredentialStatus.Success ? "ok" : created.Status.ToString());
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
            await KeyCredentialManager.DeleteAsync(name);
            Console.WriteLine("ok");
            return 0;

        default:
            Console.Error.WriteLine("usage: Cockpit.Hello supported | create <name> <hwnd> | sign <name> <hwnd> <challenge-base64> | delete <name>");
            return 2;
    }
}
catch (Exception exception)
{
    // DeleteAsync on a key that is gone, an older Windows build, a remote session: the cockpit falls back to the password.
    Console.WriteLine("error " + exception.GetType().Name);
    return 1;
}

static WindowId _Window(string hwnd) => new(ulong.Parse(hwnd, System.Globalization.CultureInfo.InvariantCulture));

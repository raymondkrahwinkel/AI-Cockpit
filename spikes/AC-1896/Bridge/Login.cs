// AC-1896 spike: sign in to the local Depot, on the web tab and for the .NET hub client. The secret comes from a file outside the
// repository whose path is an argument; it is never printed or logged.
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

internal static class Login
{
    public const string Email = "admin@depot.local";

    public static async Task WebAsync(IPage page, string baseUrl, string secretFile)
    {
        await page.GotoAsync($"{baseUrl}/login");
        await page.FillAsync("input[name=email]", Email);
        await page.FillAsync("input[name=password]", File.ReadAllText(secretFile).Trim());
        await page.PressAsync("input[name=password]", "Enter");
        await page.WaitForURLAsync(u => !u.Contains("/login"));
    }

    public static async Task<CookieContainer> HttpAsync(string baseUrl, string secretFile)
    {
        var cookies = new CookieContainer();
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = new Uri(baseUrl) };
        var page = await http.GetStringAsync("/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["returnUrl"] = "/", ["email"] = Email,
            ["password"] = File.ReadAllText(secretFile).Trim(), ["rememberMe"] = "false",
        });
        var signIn = await http.PostAsync("/login", form);
        if ((int)signIn.StatusCode is not (302 or 303))
        {
            throw new InvalidOperationException($"sign-in answered {(int)signIn.StatusCode}");
        }
        return cookies;
    }
}

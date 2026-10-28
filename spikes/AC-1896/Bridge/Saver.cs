// AC-1896 spike, for B7 (DEP-531) / DEP-533 point 2: can Cockpit be seeder and saver (save=true)? On a local Depot: .NET joins an empty
// room first, seeds it from the file's Markdown (Depot's parseBody in Jint), types, serializes with Depot's serializeNote and saves through
// note-save; then the file must be what the web tab shows, byte for byte. Cookie route here; the bearer route (/cockpit/...) is LB-1's.
using System.Net;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.Playwright;

internal static class Saver
{
    public static async Task<int> RunAsync(string baseUrl, string secretFile, string bundle, string fixture)
    {
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length == 0 ? "" : "  " + detail)}");
        }
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("PW_CHROMIUM") });
        await using var context = await browser.NewContextAsync(new() { BypassCSP = true });
        var page = await context.NewPageAsync();
        await Login.WebAsync(page, baseUrl, secretFile);
        var cookies = await Login.HttpAsync(baseUrl, secretFile);
        var path = $"ac1896/{DateTime.Now:HHmmss}-saver.md";
        Console.WriteLine($"     create: {(await Hub.CreateAsync(page, baseUrl, path, File.ReadAllText(fixture)))[..3]}");
        var notePage = $"{baseUrl}/projects/{Hub.Org}/{Hub.Project}/view/{path}?mode=collab";
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(baseUrl) };
        var html = await http.GetStringAsync(notePage);
        var userId = Regex.Match(html, "data-user=\"([0-9a-f-]{36})\"").Groups[1].Value;
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;

        var room = new JintRoom(bundle, (uint)Random.Shared.Next(1, int.MaxValue));
        await using var client = new NoteClient(baseUrl, cookies, userId);
        var join = await client.JoinAsync(Hub.Org, Hub.Project, path, "77f8a24281ff", save: true);
        var saver = join.Saver == client.ConnectionId;
        Check("saver: .NET is seeder and saver", join.Seed && saver, $"seed={join.Seed} saver-is-me={saver}");
        room.Call("seedMarkdown", join.Markdown);
        foreach (var u in room.TakeUpdates())
        {
            await client.UpdateAsync(u);
        }
        room.Call("loadMirror");
        var mirror = room.Call("readNote").AsString();
        var at = mirror.IndexOf("Notities", StringComparison.Ordinal) + "Notities".Length;
        room.Call("edit", at, 0, " (bewaard door Cockpit)");
        await client.EditedAsync();
        foreach (var u in room.TakeUpdates())
        {
            await client.UpdateAsync(u);
        }
        var content = room.Call("readNote").AsString();
        var baseChecksum = join.Raw.TryGetProperty("base", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.String ? b.GetString()! : join.Raw.GetProperty("checksum").GetString()!;
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["path"] = path, ["baseChecksum"] = baseChecksum, ["content"] = content,
            ["session"] = join.Raw.GetProperty("sessionId").GetString()!, ["__RequestVerificationToken"] = token,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/admin/projects/{Hub.Org}/{Hub.Project}/note-save") { Content = form };
        request.Headers.Add("X-Depot-User", userId);
        var saved = await http.SendAsync(request);
        Check("saver: note-save answers 200", saved.StatusCode == HttpStatusCode.OK, $"{(int)saved.StatusCode} {(await saved.Content.ReadAsStringAsync())[..Math.Min(140, (int)(saved.Content.Headers.ContentLength ?? 140))]}");

        // The file now, through the source view, and what a web tab draws and would write.
        await page.GotoAsync(notePage);
        var web = await Hub.WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 0);
        await page.AddScriptTagAsync(new() { Path = bundle });
        var webWritten = await page.EvaluateAsync<string>("([fm, j, eol]) => DepotNotes.serialize(fm, j, eol)",
            new object[] { web!.Value.GetProperty("frontmatter").GetString()!, web.Value.GetProperty("json").GetString()!, web.Value.GetProperty("eol").GetString()! });
        // The stored file's checksum (in note-save's answer) is the SHA-256 of exactly the bytes Cockpit sent (saver.ts makes the same check).
        var answer = System.Text.Json.JsonDocument.Parse(await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("checksum").GetString();
        Check("saver: the stored file is byte for byte what Cockpit serialized", answer == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content))));
        Check("saver: the web tab draws Cockpit's edit", web.Value.GetProperty("json").GetString()!.Contains("(bewaard door Cockpit)"));
        Check("saver: what the web would write == what Cockpit saved", webWritten == content, Offline.FirstDiff(content, webWritten));
        Console.WriteLine(failed == 0 ? "saver PASS" : $"saver FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }
}

// AC-1896 spike: G1 and G2 against a local Depot (never production), with a Playwright web tab running Depot's own editor.
//   G1  .NET joins an empty room first (seed=true), builds `default` from *.pm.json and sends it as a v1 update; the web tab then opens
//       the note, draws it, and its getJSON() and serializeNote (Depot's markdown.ts, injected) are compared with the fixture.
//   G2  the web tab opens the note first and seeds the room from the file; .NET joins (save=false) and reads the fragment back.
// --engine jint (room.js: yjs + y-tiptap in Jint) or ydotnet (YDotNet 0.6.0); --v2 sends Yjs v2 bytes (the counter-proof).
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Microsoft.Playwright;
using YDotNet.Document;
using YDotNet.Document.Cells;
using YDotNet.Document.Options;

internal static class Hub
{
    public const string Org = "wispslate";
    public const string Project = "eve-together";

    // The web tab's own document: the editor's JSON, and the front matter and eol from its Y.Doc (editor.ts `written`).
    public const string WebState = """
        () => {
          const ed = document.querySelector('.ProseMirror')?.editor;
          const ext = ed?.extensionManager.extensions.find(e => e.name === 'collaboration');
          if (!ext) return null;
          const doc = ext.options.document;
          return { json: JSON.stringify(ed.getJSON()), frontmatter: doc.getText('frontmatter').toString(),
                   eol: doc.getMap('meta').get('eol') === '\r\n' ? '\r\n' : '\n', blocks: ed.state.doc.childCount,
                   fragment: doc.getXmlFragment('default').length, text: document.querySelector('.ProseMirror').innerText.length };
        }
        """;

    public static async Task<int> RunAsync(string baseUrl, string secretFile, string bundle, string fixtures, string engine, bool v2, string only)
    {
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length == 0 ? "" : "  " + detail)}");
        }
        var judge = new JintRoom(bundle, 1);
        var schema = judge.Notes("schemaJson") is var s ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant() : "";
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true, ExecutablePath = Environment.GetEnvironmentVariable("PW_CHROMIUM") });
        var run = DateTime.Now.ToString("HHmmss");
        var cookies = await Login.HttpAsync(baseUrl, secretFile);
        string? userId = null;

        foreach (var md in Directory.GetFiles(fixtures, "*.md").Where(f => !f.EndsWith("SOURCE.md") && f.Contains(only)).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(md);
            var text = File.ReadAllText(md);
            var pm = File.ReadAllText(Path.ChangeExtension(md, ".pm.json")).Replace("\r\n", "\n");
            var normalPm = judge.Call("normalize", pm).AsString();
            var frontmatter = judge.Eval($"DepotNotes.splitFrontmatter({JsonSerializer.Serialize(text)}).frontmatter");
            var eol = text.Contains("\r\n") ? "\r\n" : "\n";

            string? webSeeded = null;
            foreach (var g in new[] { "G2", "G1" })
            {
                // A fresh context per check: one web tab, its own cookie, nothing left from the run before.
                await using var context = await browser.NewContextAsync(new() { BypassCSP = true });
                var page = await context.NewPageAsync();
                await Login.WebAsync(page, baseUrl, secretFile);
                var path = $"ac1896/{run}-{engine}-{g}-{name}.md";
                var created = await CreateAsync(page, baseUrl, path, text);
                if (!created.StartsWith("200"))
                {
                    Check($"{g} {name} create {path}", false, created);
                    continue;
                }
                var notePage = $"{baseUrl}/projects/{Org}/{Project}/view/{path}?mode=collab";
                userId ??= await UserIdAsync(baseUrl, cookies, notePage);
                await using var client = new NoteClient(baseUrl, cookies, userId);

                if (g == "G1")
                {
                    var join = await client.JoinAsync(Org, Project, path, schema, save: true);
                    Check($"G1 {name} .NET is the seeder", join.Seed && join.Role == "editor" && !join.SchemaMismatch, $"seed={join.Seed} role={join.Role} mismatch={join.SchemaMismatch} file==fixture:{join.Markdown == text}");
                    var update = engine == "jint" ? SeedJint(bundle, join.Rows, pm, frontmatter, eol, v2) : SeedYdn(join.Rows, pm, frontmatter, eol, v2);
                    await client.UpdateAsync(update);
                    Console.WriteLine($"     G1 {name} sent {update.Length} B ({(v2 ? "v2" : "v1")})");

                    await page.GotoAsync(notePage);
                    var web = await WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 0 && w.GetProperty("blocks").GetInt32() > 1);
                    if (web is null)
                    {
                        Check($"G1 {name} web tab draws it", false, "the web's fragment stayed empty (8 s)");
                        continue;
                    }
                    await page.AddScriptTagAsync(new() { Path = bundle });
                    var json = web.Value.GetProperty("json").GetString()!;
                    var webNormal = await page.EvaluateAsync<string>("(j) => DepotNotes.room.normalize(j)", json);
                    var written = await page.EvaluateAsync<string>("([fm, j, eol]) => DepotNotes.serialize(fm, j, eol)",
                        new object[] { web.Value.GetProperty("frontmatter").GetString()!, json, web.Value.GetProperty("eol").GetString()! });
                    Check($"G1 {name} web tab draws it", web.Value.GetProperty("text").GetInt32() > 0, $"{web.Value.GetProperty("blocks").GetInt32()} blocks");
                    // Not a gate: the web editor's TrailingNode adds an empty paragraph after a last block that is not one, also when the web
                    // seeds the file itself (G2's baseline shows it); the gate is the comparison with the web's own seed below.
                    Console.WriteLine($"     G1 {name} web getJSON == pm.json (schema form): {webNormal == normalPm} {Offline.FirstDiff(normalPm, webNormal)}");
                    // The same editor on the same file, seeded by itself (G2's tab): what the web holds when no .NET is involved.
                    Check($"G1 {name} web getJSON == the web's own seed of the file", webSeeded is not null && json == webSeeded, webSeeded is null ? "no G2 baseline" : Offline.FirstDiff(webSeeded, json));
                    Check($"G1 {name} web serializeNote == file (byte-equal)", written == text, Offline.FirstDiff(text, written));
                }
                else
                {
                    await page.GotoAsync(notePage);
                    var web = await WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 0);
                    await Task.Delay(1500); // the seed goes out as an Update right after the web tab builds it
                    webSeeded = (await page.EvaluateAsync<JsonElement?>(WebState))?.GetProperty("json").GetString();
                    var webSeedNormal = webSeeded is null ? "" : judge.Call("normalize", webSeeded).AsString();
                    Console.WriteLine($"     G2 {name} the web's own seed == pm.json (schema form): {webSeedNormal == normalPm} {Offline.FirstDiff(normalPm, webSeedNormal)}");
                    var join = await client.JoinAsync(Org, Project, path, schema, save: false);
                    Check($"G2 {name} web seeded, .NET joined", web is not null && join.Rows.Count > 0 && join.Role == "editor",
                        $"rows={join.Rows.Count} role={join.Role} saver-is-me={join.Saver == client.ConnectionId} file==fixture:{join.Markdown == text}");
                    var read = engine == "jint" ? ReadJint(bundle, join.Rows) : ReadYdn(join.Rows);
                    string normal;
                    try
                    {
                        normal = judge.Call("normalize", read).AsString();
                    }
                    catch (Exception e)
                    {
                        normal = "schema refused: " + e.Message;
                    }
                    Check($"G2 {name} .NET reads pm.json (schema form)", normal == normalPm, Offline.FirstDiff(normalPm, normal));
                    if (engine == "jint")
                    {
                        var reader = new JintRoom(bundle, 9);
                        join.Rows.ForEach(reader.Apply);
                        var note = reader.Call("readNote").AsString();
                        Console.WriteLine($"     G2 {name} .NET serializeNote == file: {note == text} {Offline.FirstDiff(text, note)}");
                    }
                }
            }
        }
        Console.WriteLine(failed == 0 ? $"hub {engine} PASS" : $"hub {engine} FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }

    private static byte[] SeedJint(string bundle, List<byte[]> rows, string pm, string frontmatter, string eol, bool v2)
    {
        var room = new JintRoom(bundle, (uint)Random.Shared.Next(1, int.MaxValue));
        rows.ForEach(room.Apply);
        room.Call("seedPm", pm, frontmatter, eol);
        room.TakeUpdates();
        return Convert.FromBase64String(room.Call(v2 ? "missingV2" : "missing", JsonSerializer.Serialize(rows.Select(Convert.ToBase64String))).AsString());
    }

    private static byte[] SeedYdn(List<byte[]> rows, string pm, string frontmatter, string eol, bool v2)
    {
        using var doc = new Doc(new DocOptions { Id = (ulong)Random.Shared.Next(1, int.MaxValue), Encoding = DocEncoding.Utf16 });
        var fragment = doc.XmlFragment("default");
        var fm = doc.Text("frontmatter");
        var meta = doc.Map("meta");
        using var scratch = new Doc();
        foreach (var row in rows)
        {
            using (var t = doc.WriteTransaction())
            {
                t.ApplyV1(row);
                t.Commit();
            }
            using (var t = scratch.WriteTransaction())
            {
                t.ApplyV1(row);
                t.Commit();
            }
        }
        using (var tx = doc.WriteTransaction())
        {
            YdnFragment.Write(tx, fragment, JsonNode.Parse(pm)!);
            if (frontmatter.Length > 0)
            {
                fm.Insert(tx, 0, frontmatter);
            }
            using var e = Input.String(eol);
            meta.Insert(tx, "eol", e);
            tx.Commit();
        }
        byte[] vector;
        using (var t = scratch.ReadTransaction())
        {
            vector = t.StateVectorV1();
        }
        using var read = doc.ReadTransaction();
        return v2 ? read.StateDiffV2(vector) : read.StateDiffV1(vector);
    }

    private static string ReadJint(string bundle, List<byte[]> rows)
    {
        var room = new JintRoom(bundle, 8);
        rows.ForEach(room.Apply);
        return room.Call("readPm").AsString();
    }

    private static string ReadYdn(List<byte[]> rows)
    {
        using var doc = new Doc(new DocOptions { Id = 8, Encoding = DocEncoding.Utf16 });
        var fragment = doc.XmlFragment("default");
        foreach (var row in rows)
        {
            using var t = doc.WriteTransaction();
            t.ApplyV1(row);
            t.Commit();
        }
        using var tx = doc.ReadTransaction();
        return YdnFragment.Read(tx, fragment).ToJsonString();
    }

    public static async Task<JsonElement?> WaitWebAsync(IPage page, Func<JsonElement, bool> ready, int ms = 8000)
    {
        for (var waited = 0; waited < ms; waited += 200)
        {
            var state = await page.EvaluateAsync<JsonElement?>(WebState);
            if (state is { ValueKind: JsonValueKind.Object } s && ready(s))
            {
                await Task.Delay(300);
                return await page.EvaluateAsync<JsonElement?>(WebState);
            }
            await Task.Delay(200);
        }
        return null;
    }

    // A new note through the browser's own document route (DEP-260), with the page's antiforgery token.
    public static async Task<string> CreateAsync(IPage page, string baseUrl, string path, string content)
    {
        await page.GotoAsync($"{baseUrl}/projects/{Org}/{Project}");
        return await page.EvaluateAsync<string>("""
            async ([org, project, path, content]) => {
              const token = document.querySelector('input[name="__RequestVerificationToken"]')?.value ?? '';
              const body = new URLSearchParams({ path, baseChecksum: 'new', content, __RequestVerificationToken: token });
              const r = await fetch(`/admin/projects/${org}/${project}/document`, { method: 'POST', body });
              return `${r.status} ${(await r.text()).slice(0, 160)}`;
            }
            """, new object[] { Org, Project, path, content });
    }

    public static async Task<string> UserIdAsync(string baseUrl, CookieContainer cookies, string notePage)
    {
        using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies });
        var html = await http.GetStringAsync(notePage);
        return Regex.Match(html, "data-user=\"([0-9a-f-]{36})\"").Groups[1].Value;
    }
}

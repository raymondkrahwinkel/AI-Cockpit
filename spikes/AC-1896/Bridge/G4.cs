// AC-1896 spike, G4: one keystroke in a paragraph of a 2,000-line note, through the incremental bridge (room.js `edit`).
//   offline  the cost: time per keystroke (Markdown patch -> Y update), update size, other blocks untouched, mirror still exact
//   hub      on a local Depot: a web caret in another block stays where it was while .NET types
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Jint;
using Microsoft.Playwright;

internal static class G4
{
    public static string BigNote()
    {
        var big = new StringBuilder("---\ntitle: Big\n---\n\n# Big note\n\n");
        for (var i = 0; big.ToString().Count(c => c == '\n') < 2000; i++)
        {
            big.Append((i % 5) switch
            {
                0 => $"Paragraph {i} with **bold**, *italic*, `code` and a [link](https://example.org/{i}). Some more words to make it a line.\n\n",
                1 => $"- item {i}\n  - nested {i}\n- [ ] task {i}\n\n",
                2 => $"## Heading {i}\n\n",
                3 => $"| a | b |\n| --- | --- |\n| {i} | x |\n\n",
                _ => $"> quote {i}\n\n```cs\nvar x = {i};\n```\n\n",
            });
        }
        return big.ToString().TrimEnd('\n') + "\n";
    }

    public static int Offline(string bundle)
    {
        var failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            failed += ok ? 0 : 1;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length == 0 ? "" : "  " + detail)}");
        }
        var note = BigNote();
        var sw = Stopwatch.StartNew();
        var room = new JintRoom(bundle, 11);
        var loadMs = sw.Elapsed.TotalMilliseconds;
        room.Call("seedMarkdown", note);
        var seedMs = sw.Elapsed.TotalMilliseconds - loadMs;
        room.TakeUpdates();
        sw.Restart();
        var mirror = JsonDocument.Parse(room.Call("loadMirror").AsString()).RootElement;
        Console.WriteLine($"     note: {note.Count(c => c == '\n')} lines, {note.Length / 1024} KiB; bundle load {loadMs:F0} ms, seed {seedMs:F0} ms, " +
            $"mirror {sw.Elapsed.TotalMilliseconds:F0} ms ({mirror.GetProperty("found").GetInt32()}/{mirror.GetProperty("blocks").GetInt32()} blocks found in the text)");
        Check("G4 every block found in the mirror", mirror.GetProperty("found").GetInt32() == mirror.GetProperty("blocks").GetInt32());
        Check("G4 mirror == serializeNote after load", room.Call("mirrorIsExact").AsBoolean());

        // Type 60 characters, one patch each, at the end of a paragraph's first sentence in the middle of the note.
        var before = JsonDocument.Parse(room.Call("readPm").AsString()).RootElement.GetProperty("content").EnumerateArray().Select(b => b.GetRawText()).ToList();
        var anchor = note.IndexOf("Paragraph 500 with", StringComparison.Ordinal) + "Paragraph 500".Length;
        var times = new List<double>();
        var sizes = new List<int>();
        var hit = new HashSet<int>();
        for (var i = 0; i < 60; i++)
        {
            sw.Restart();
            var result = JsonDocument.Parse(room.Call("edit", anchor + i, 0, ((char)('a' + i % 26)).ToString()).AsString()).RootElement;
            var updates = room.TakeUpdates();
            times.Add(sw.Elapsed.TotalMilliseconds);
            sizes.Add(updates.Sum(u => u.Length));
            hit.Add(result.GetProperty("block").GetInt32());
        }
        var after = JsonDocument.Parse(room.Call("readPm").AsString()).RootElement.GetProperty("content").EnumerateArray().Select(b => b.GetRawText()).ToList();
        var changed = Enumerable.Range(0, Math.Max(before.Count, after.Count)).Where(i => i >= before.Count || i >= after.Count || before[i] != after[i]).ToList();
        static double P(List<double> xs, double q) => xs.Order().ElementAt((int)Math.Ceiling(q * xs.Count) - 1);
        Console.WriteLine($"     keystroke: p50 {P(times, .5):F1} ms, p95 {P(times, .95):F1} ms, max {times.Max():F1} ms (first {times[0]:F1} ms); " +
            $"update p50 {sizes.Order().ElementAt(30)} B, max {sizes.Max()} B; blocks hit {string.Join(",", hit)}");
        Check("G4 update < 2 KB", sizes.Max() < 2048, $"max {sizes.Max()} B");
        Check("G4 p95 < 30 ms", P(times, .95) < 30, $"p95 {P(times, .95):F1} ms");
        Check("G4 only the typed block changed", changed.Count == 1 && hit.Count == 1 && hit.Single() == changed[0], $"changed blocks {string.Join(",", changed)}");
        Check("G4 mirror == serializeNote after typing", room.Call("mirrorIsExact").AsBoolean());
        Console.WriteLine(failed == 0 ? "g4 offline PASS" : $"g4 offline FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }

    public static async Task<int> HubAsync(string baseUrl, string secretFile, string bundle)
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
        var path = $"ac1896/{DateTime.Now:HHmmss}-g4-big.md";
        var note = BigNote();
        Console.WriteLine($"     create: {await Hub.CreateAsync(page, baseUrl, path, note)}");
        var notePage = $"{baseUrl}/projects/{Hub.Org}/{Hub.Project}/view/{path}?mode=collab";
        await page.GotoAsync(notePage);
        await Hub.WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 100, 20000);
        await Task.Delay(1500);

        var room = new JintRoom(bundle, (uint)Random.Shared.Next(1, int.MaxValue));
        await using var client = new NoteClient(baseUrl, cookies, await Hub.UserIdAsync(baseUrl, cookies, notePage));
        client.OnUpdate = room.Apply;
        var schema = "77f8a24281ff";
        var join = await client.JoinAsync(Hub.Org, Hub.Project, path, schema, save: false);
        join.Rows.ForEach(room.Apply);
        room.TakeUpdates();
        Console.WriteLine($"     joined: {join.Rows.Count} rows, mirror {room.Call("loadMirror").AsString()}");

        // The web caret in paragraph 540, five characters in: after the block .NET types in, so its absolute position moves.
        const string Caret = """
            () => { const ed = document.querySelector('.ProseMirror').editor; const s = ed.state.selection.$anchor;
                    return JSON.stringify({ text: s.parent.textContent, offset: s.parentOffset, pos: ed.state.selection.anchor }); }
            """;
        await page.EvaluateAsync("""
            () => { const ed = document.querySelector('.ProseMirror').editor; let at = -1;
                    ed.state.doc.descendants((n, pos) => { if (at < 0 && n.isTextblock && n.textContent.startsWith('Paragraph 540 ')) at = pos + 1 + 5; });
                    ed.commands.focus(); ed.commands.setTextSelection(at); }
            """);
        var caretBefore = await page.EvaluateAsync<string>(Caret);

        // Offsets are in the room's text (the server rewrote the front matter on create), never in the file as it was sent.
        var mirrorText = room.Call("readNote").AsString();
        var sentDiff = global::Offline.FirstDiff(note, mirrorText);
        Console.WriteLine($"     room text == the file sent: {mirrorText == note} {sentDiff}");
        var anchor = mirrorText.IndexOf("Paragraph 500 with", StringComparison.Ordinal) + "Paragraph 500".Length;
        var typed = "-dotnet-typed-";
        for (var i = 0; i < typed.Length; i++)
        {
            room.Call("edit", anchor + i, 0, typed[i].ToString());
            await client.EditedAsync();
            foreach (var u in room.TakeUpdates())
            {
                await client.UpdateAsync(u);
            }
            await Task.Delay(50);
        }
        await Task.Delay(1500);
        Console.WriteLine($"     refusals: [{string.Join(",", client.Refusals)}]; .NET text: {room.Call("readNote").AsString() is var n && n.Contains("Paragraph 500" + typed)}");
        Console.WriteLine($"     web around: {await page.EvaluateAsync<string>("() => { const t = document.querySelector('.ProseMirror').innerText; const i = t.indexOf('Paragraph 500'); return JSON.stringify(t.slice(i, i + 50)); }")}");
        var webHas = await page.EvaluateAsync<bool>("(t) => document.querySelector('.ProseMirror').innerText.includes('Paragraph 500' + t)", typed);
        var caretAfter = await page.EvaluateAsync<string>(Caret);
        var b = JsonDocument.Parse(caretBefore).RootElement;
        var a = JsonDocument.Parse(caretAfter).RootElement;
        Check("G4 web shows the .NET keystrokes", webHas);
        Check("G4 web caret stays on the same character of its own block", a.GetProperty("text").GetString() == b.GetProperty("text").GetString() && a.GetProperty("offset").GetInt32() == b.GetProperty("offset").GetInt32(),
            $"pos {b.GetProperty("pos").GetInt32()} -> {a.GetProperty("pos").GetInt32()}, offset {b.GetProperty("offset").GetInt32()} -> {a.GetProperty("offset").GetInt32()}");
        Console.WriteLine(failed == 0 ? "g4 hub PASS" : $"g4 hub FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }
}

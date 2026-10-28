// AC-1896 spike, G5: a web edit becomes a text patch (offset, length, text) on an AvaloniaEdit TextDocument, headless.
//   - the host caret is a TextAnchor; after remote patches it must sit on the same character
//   - undo goes through a Y.UndoManager in the room that only tracks the host's own edits, so it skips the remote patch
//   - for comparison: AvaloniaEdit's own UndoStack, which would undo the remote patch (what LB-2 must not use as is)
using System.Collections.Concurrent;
using System.Text.Json;
using AvaloniaEdit.Document;
using Jint;
using Microsoft.Playwright;

internal static class G5
{
    private sealed record Patch(int offset, int removed, string inserted);

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
        var path = $"ac1896/{DateTime.Now:HHmmss}-g5.md";
        Console.WriteLine($"     create: {(await Hub.CreateAsync(page, baseUrl, path, File.ReadAllText(fixture)))[..3]}");
        var notePage = $"{baseUrl}/projects/{Hub.Org}/{Hub.Project}/view/{path}?mode=collab";
        await page.GotoAsync(notePage);
        await Hub.WaitWebAsync(page, w => w.GetProperty("fragment").GetInt32() > 0);
        await Task.Delay(1500);

        var room = new JintRoom(bundle, (uint)Random.Shared.Next(1, int.MaxValue));
        var inbox = new ConcurrentQueue<byte[]>();
        await using var client = new NoteClient(baseUrl, cookies, await Hub.UserIdAsync(baseUrl, cookies, notePage));
        var join = await client.JoinAsync(Hub.Org, Hub.Project, path, "77f8a24281ff", save: false);
        join.Rows.ForEach(room.Apply);
        room.TakeUpdates();
        room.Call("loadMirror");
        room.Call("trackUndo");
        client.OnUpdate = inbox.Enqueue;

        // The host's document, filled once from the mirror; from here on it only ever changes by patches.
        var text = new TextDocument(room.Call("readNote").AsString());
        text.UndoStack.SizeLimit = 0; // the host's own undo is the room's; AvaloniaEdit's stack would also undo remote patches
        var caretAt = text.Text.IndexOf("Raymond: mockups", StringComparison.Ordinal) + "Raymond: mock".Length;
        var caret = text.CreateAnchor(caretAt);
        var caretChar = text.GetCharAt(caret.Offset);
        var caretContext = text.GetText(caret.Offset - 8, 16);
        async Task PumpAsync(int ms)
        {
            for (var waited = 0; waited < ms; waited += 50)
            {
                while (inbox.TryDequeue(out var update))
                {
                    foreach (var p in JsonSerializer.Deserialize<Patch[]>(room.Call("applyRemote", Convert.ToBase64String(update)).AsString())!)
                    {
                        text.Replace(p.offset, p.removed, p.inserted);
                    }
                }
                await Task.Delay(50);
            }
        }

        // 1. The host types in the first heading: a local edit, into the TextDocument and the room, out to the hub.
        var local = "LOCAL ";
        var localAt = text.Text.IndexOf("# Sprintreview", StringComparison.Ordinal) + 2;
        text.Insert(localAt, local);
        room.Call("edit", localAt, 0, local);
        await client.EditedAsync();
        foreach (var u in room.TakeUpdates())
        {
            await client.UpdateAsync(u);
        }
        await PumpAsync(800);

        // 2. The web types twice: in the caret's own line before the caret, and in an earlier block.
        await page.EvaluateAsync("""
            () => { const ed = document.querySelector('.ProseMirror').editor; let at = -1;
                    ed.state.doc.descendants((n, pos) => { if (at < 0 && n.isText && n.text.includes('Raymond: mockups')) at = pos + n.text.indexOf('Raymond'); });
                    ed.commands.focus(); ed.commands.setTextSelection(at); }
            """);
        await page.Keyboard.TypeAsync("WEB1 ", new() { Delay = 40 });
        await page.EvaluateAsync("""
            () => { const ed = document.querySelector('.ProseMirror').editor; let at = -1;
                    ed.state.doc.descendants((n, pos) => { if (at < 0 && n.isText && n.text.startsWith('Sprintreview')) at = pos; });
                    ed.commands.setTextSelection(at); }
            """);
        await page.Keyboard.TypeAsync("WEB2 ", new() { Delay = 40 });
        await PumpAsync(2500);

        var exact = text.Text == room.Call("readNote").AsString();
        Check("G5 TextDocument == the room's serializeNote after remote patches", exact, exact ? "" : Offline.FirstDiff(room.Call("readNote").AsString(), text.Text));
        Console.WriteLine($"     text now: {JsonSerializer.Serialize(text.Text[..Math.Min(520, text.Text.Length)])}");
        Check("G5 remote text arrived", text.Text.Contains("- [ ] WEB1 Raymond") && text.Text.Contains("# WEB2 LOCAL Sprintreview"));
        Check("G5 caret (TextAnchor) on the same character", !caret.IsDeleted && text.GetCharAt(caret.Offset) == caretChar && text.GetText(caret.Offset - 8, 16) == caretContext,
            $"offset {caretAt} -> {caret.Offset}, context \"{text.GetText(caret.Offset - 8, 16)}\"");

        // 3. Undo: the room's UndoManager removes the host's LOCAL and leaves both web edits.
        foreach (var p in JsonSerializer.Deserialize<Patch[]>(room.Call("undo").AsString())!)
        {
            text.Replace(p.offset, p.removed, p.inserted);
        }
        foreach (var u in room.TakeUpdates())
        {
            await client.UpdateAsync(u);
        }
        await PumpAsync(1500);
        Check("G5 undo removes the host's own edit", !text.Text.Contains(local + "Sprintreview") && !text.Text.Contains("LOCAL"));
        Check("G5 undo skips the remote patches", text.Text.Contains("- [ ] WEB1 Raymond") && text.Text.Contains("# WEB2 Sprintreview"));
        var web = await page.EvaluateAsync<string>("() => document.querySelector('.ProseMirror').innerText");
        Check("G5 the web sees the undo", !web.Contains("LOCAL") && web.Contains("WEB1"));
        Check("G5 TextDocument == serializeNote after undo", text.Text == room.Call("readNote").AsString());

        // For comparison, AvaloniaEdit's own stack on a copy: it records the remote patch like any edit and undoes it first.
        var naive = new TextDocument("abc");
        naive.Insert(3, "LOCAL");
        naive.Insert(0, "WEB");
        naive.UndoStack.Undo();
        Console.WriteLine($"     AvaloniaEdit UndoStack after local+remote, one Undo: \"{naive.Text}\" (it undid the remote insert)");
        Console.WriteLine(failed == 0 ? "g5 PASS" : $"g5 FAIL ({failed})");
        return failed == 0 ? 0 : 1;
    }
}

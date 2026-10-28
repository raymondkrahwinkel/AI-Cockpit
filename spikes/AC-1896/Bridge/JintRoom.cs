// AC-1896 spike: one Jint engine holding the bridge bundle (markdown.ts + room.js). One room per engine, and one caller at a time:
// Jint is single-threaded, so the hub handlers and the command loop go through the same lock (the lesson of AC-1576 for the Y.Doc).
using Jint;
using Jint.Native;

internal sealed class JintRoom
{
    private readonly Engine _engine = new();
    private readonly JsValue _notes;
    private readonly JsValue _room;
    public readonly object Gate = new();

    public JintRoom(string bundle, uint clientId)
    {
        // lib0 (under yjs) takes crypto.getRandomValues at load; Jint has no Web Crypto. Only ids come from it here (client ids are set).
        _engine.Execute("globalThis.crypto = { subtle: {}, getRandomValues: (a) => { for (let i = 0; i < a.length; i++) a[i] = Math.floor(Math.random() * 4294967296); return a; } };");
        _engine.Execute(File.ReadAllText(bundle));
        _notes = _engine.GetValue("DepotNotes");
        _room = _notes.AsObject().Get("room");
        Call("createRoom", clientId);
    }

    public JsValue Call(string fn, params object?[] args)
    {
        lock (Gate)
        {
            return _engine.Invoke(_room.AsObject().Get(fn), args);
        }
    }

    public string Notes(string fn, params object?[] args)
    {
        lock (Gate)
        {
            return _engine.Invoke(_notes.AsObject().Get(fn), args).AsString();
        }
    }

    public void Apply(byte[] update) => Call("apply", Convert.ToBase64String(update));

    public List<byte[]> TakeUpdates() =>
        System.Text.Json.JsonSerializer.Deserialize<string[]>(Call("takeUpdates").AsString())!.Select(Convert.FromBase64String).ToList();

    public string Eval(string script)
    {
        lock (Gate)
        {
            return _engine.Evaluate(script).ToString();
        }
    }
}

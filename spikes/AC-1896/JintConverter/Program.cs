// AC-1896 spike (G3): Depot's own markdown.ts + merge.ts, bundled by ../bundle/build.mjs, run in Jint (pure .NET, no DOM).
// Usage: JintConverter <bundle.js> <fixtures dir>
// Checks per fixture what scripts/notes-roundtrip-check.mjs checks: parse deep-equal to *.pm.json (as the same JSON text), the
// round-trip byte-equal to the file, and also the serialization of *.pm.json itself byte-equal to the file. Then the schema
// version, a merge smoke, and the cost: cold start, memory, and serialize/parse time on a note of 2,000 lines.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Jint;
using Jint.Native;

var (bundlePath, fixtures) = (args[0], args[1]);
var failed = 0;
void Check(string name, bool ok, string detail = "")
{
    if (!ok)
    {
        failed++;
    }
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {name}{(detail.Length > 0 ? $"  {detail}" : "")}");
}

var memBefore = GC.GetTotalMemory(true);
var privateBefore = Process.GetCurrentProcess().PrivateMemorySize64;
var cold = Stopwatch.StartNew();
var engine = new Engine(o => o.Strict(false));
engine.Execute(File.ReadAllText(bundlePath));
var notes = engine.GetValue("DepotNotes").AsObject();
JsValue Call(string fn, params object?[] a) => engine.Invoke(notes.Get(fn), a);
var loadMs = cold.Elapsed.TotalMilliseconds;
Call("parse", "# warm\n");
var firstCallMs = cold.Elapsed.TotalMilliseconds;
var memAfter = GC.GetTotalMemory(true);
var privateAfter = Process.GetCurrentProcess().PrivateMemorySize64;
Console.WriteLine($"cost: bundle {new FileInfo(bundlePath).Length / 1024} KiB, load {loadMs:F0} ms, load+first parse {firstCallMs:F0} ms, " +
    $"managed heap +{(memAfter - memBefore) / 1048576.0:F1} MiB, private bytes +{(privateAfter - privateBefore) / 1048576.0:F1} MiB");

static string Lf(string s) => s.Replace("\r\n", "\n");
foreach (var md in Directory.GetFiles(fixtures, "*.md").Where(f => !f.EndsWith("SOURCE.md")).Order(StringComparer.Ordinal))
{
    var name = Path.GetFileName(md);
    var text = File.ReadAllText(md, new UTF8Encoding(false));
    var expectedPm = Lf(File.ReadAllText(Path.ChangeExtension(md, ".pm.json")));
    var parsed = Call("parse", text).AsString();
    Check($"{name} parse == pm.json", parsed == expectedPm, parsed == expectedPm ? "" : FirstDiff(expectedPm, parsed));
    var again = Call("roundTrip", text).AsString();
    Check($"{name} roundTrip == file", again == text, again == text ? "" : FirstDiff(text, again));
    var split = Call("splitFrontmatter", text).AsObject();
    var fromPm = Call("serialize", split.Get("frontmatter").AsString(), expectedPm, Call("lineEnding", text).AsString()).AsString();
    Check($"{name} serialize(pm.json) == file", fromPm == text, fromPm == text ? "" : FirstDiff(text, fromPm));
}

var schemaLive = Call("schemaJson").AsString();
var schemaFile = Lf(File.ReadAllText(Path.Combine(fixtures, "schema.json")));
string Version(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12].ToLowerInvariant();
Check($"schema version {Version(schemaLive)} == schema.json {Version(schemaFile)}", schemaLive == schemaFile);

// merge.ts through the same route: an outside write to one block, merged with an untouched document, keeps both sides' state.
var baseMd = "# T\n\nalpha\n\nbeta\n";
var theirs = "# T\n\nalpha changed\n\nbeta\n";
var ours = Call("parse", "# T\n\nalpha\n\nbeta ours\n").AsString();
using var merged = System.Text.Json.JsonDocument.Parse(Call("merge", baseMd, theirs, "", ours).AsString());
var mergedMd = Call("serialize", "", merged.RootElement.GetProperty("doc").GetRawText(), "\n").AsString();
Check("merge.ts three-way", mergedMd == "# T\n\nalpha changed\n\nbeta ours\n" && merged.RootElement.GetProperty("conflicts").GetInt32() == 0, Escape(mergedMd));

// The cost on a long note: 2,000 lines of mixed blocks, a full serialize and parse each time (what a naive bridge would do per key).
var big = new StringBuilder("# Big\n\n");
for (var i = 0; big.ToString().Count(c => c == '\n') < 2000; i++)
{
    big.Append((i % 4) switch
    {
        0 => $"Paragraph {i} with **bold** and a [link](https://example.org/{i}).\n\n",
        1 => $"- item {i}\n  - nested {i}\n\n",
        2 => $"## Heading {i}\n\n",
        _ => $"| a | b |\n| --- | --- |\n| {i} | x |\n\n",
    });
}
var bigMd = big.ToString().TrimEnd('\n') + "\n";
var bigPm = Call("parse", bigMd).AsString();
var parseTimes = new List<double>();
var serializeTimes = new List<double>();
for (var i = 0; i < 30; i++)
{
    var sw = Stopwatch.StartNew();
    Call("parse", bigMd);
    parseTimes.Add(sw.Elapsed.TotalMilliseconds);
    sw.Restart();
    Call("serialize", "", bigPm, "\n");
    serializeTimes.Add(sw.Elapsed.TotalMilliseconds);
}
static double P(List<double> xs, double q) => xs.Order().ElementAt((int)Math.Ceiling(q * xs.Count) - 1);
Console.WriteLine($"cost: 2,000-line note ({bigMd.Length / 1024} KiB) parse p50 {P(parseTimes, .5):F0} ms p95 {P(parseTimes, .95):F0} ms, " +
    $"serialize p50 {P(serializeTimes, .5):F0} ms p95 {P(serializeTimes, .95):F0} ms; private bytes now " +
    $"{Process.GetCurrentProcess().PrivateMemorySize64 / 1048576.0:F0} MiB");
Console.WriteLine(failed == 0 ? "G3 PASS" : $"G3 FAIL ({failed})");
return failed == 0 ? 0 : 1;

static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
static string FirstDiff(string expected, string actual)
{
    var i = 0;
    while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
    {
        i++;
    }
    string Around(string s) => Escape(s.Substring(Math.Max(0, i - 20), Math.Min(s.Length - Math.Max(0, i - 20), 40)));
    return $"first difference at char {i}: expected \"{Around(expected)}\" got \"{Around(actual)}\"";
}

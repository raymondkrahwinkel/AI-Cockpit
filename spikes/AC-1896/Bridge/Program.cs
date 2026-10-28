// AC-1896 spike: the live bridge probe. Not product code; not part of Cockpit.slnx.
//   offline <bridge.jint.js> <fixtures>                                              G1/G2 without a server (Offline.cs)
//   g4 <bridge.jint.js> | g4hub <baseUrl> <secretFile> <bridge.jint.js>                    G4 (G4.cs)
//   g5 <baseUrl> <secretFile> <bridge.jint.js> <fixture.md>                                G5 (G5.cs)
//   g6 <baseUrl> <secretFile> <bridge.jint.js> <fixture.md>                                G6 (G6.cs)
//   hub <baseUrl> <secretFile> <bridge.jint.js> <fixtures> jint|ydotnet [--v2] [--only <name>]   G1/G2 on a local Depot (Hub.cs)
var only = Array.IndexOf(args, "--only") is var o and > 0 ? args[o + 1] : "";
return args[0] switch
{
    "offline" => Offline.Run(args[1], args[2]),
    "g4" => G4.Offline(args[1]),
    "g4hub" => await G4.HubAsync(args[1], args[2], args[3]),
    "g5" => SingleThread.Run(() => G5.RunAsync(args[1], args[2], args[3], args[4])),
    "g6" => await G6.RunAsync(args[1], args[2], args[3], args[4]),
    "hub" => await Hub.RunAsync(args[1], args[2], args[3], args[4], args[5], args.Contains("--v2"), only),
    _ => throw new ArgumentException($"unknown mode {args[0]}"),
};

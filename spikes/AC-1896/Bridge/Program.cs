// AC-1896 spike: the live bridge probe. Not product code; not part of Cockpit.slnx.
//   offline <bridge.jint.js> <fixtures>                 G1/G2 without a server (Offline.cs)
return args[0] switch
{
    "offline" => Offline.Run(args[1], args[2]),
    _ => throw new ArgumentException($"unknown mode {args[0]}"),
};

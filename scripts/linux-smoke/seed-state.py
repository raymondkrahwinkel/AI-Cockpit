#!/usr/bin/env python3
"""Seeds a fresh state root for the Linux smoke test: installs the staged Browser plugin and pins it.

The pin is PluginClosureHash.OfInstalledFolderAsync's: every file below the plugin folder except dot-files, relative
path with '/', ordinal order, each field framed with a 4-byte little-endian length (PluginHash.ComputeClosure).

    seed-state.py <state-root> <staged-browser-plugin-dir>
"""
import hashlib
import json
import shutil
import struct
import sys
from pathlib import Path

PAGE = "data:text/html,<title>LinuxSmoke</title><body style=background:white><h1>Browser OK</h1>"


def closure_hash(folder: Path) -> str:
    files = sorted(
        ((p.relative_to(folder).as_posix(), hashlib.sha256(p.read_bytes()).hexdigest())
         for p in folder.rglob("*") if p.is_file() and not p.name.startswith(".")),
        key=lambda f: f[0].encode("utf-8"),
    )
    digest = hashlib.sha256()
    for path, sha in files:
        for field in (path.encode(), sha.encode()):
            digest.update(struct.pack("<i", len(field)))
            digest.update(field)
    return digest.hexdigest()


state, staged = Path(sys.argv[1]), Path(sys.argv[2])
target = state / "plugins" / "browser"
shutil.copytree(staged, target)
for exe in (target / "Subprocess").iterdir():
    if exe.name == "Cockpit.Plugin.Browser.Subprocess":
        exe.chmod(0o755)

config = {
    "Plugins": {
        "browser": {
            "Enabled": True,
            "PinnedSha256": closure_hash(target),
            "PinnedToSidebar": True,
            "VersionChosen": True,
            # startupMode 2 = open the start page; a plain page with a heading shows the engine really painted.
            "Data": {"startupMode": "2", "startPage": json.dumps(PAGE)},
        }
    }
}
(state / "cockpit.json").write_text(json.dumps(config, indent=2))

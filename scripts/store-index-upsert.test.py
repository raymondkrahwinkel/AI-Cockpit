#!/usr/bin/env python3
"""Tests for scripts/store-index-upsert.py.

AC-1289: `audience` is a curator-assigned field (AC-511) that lives only in index.json, not in any
plugin.json the generator reads. build_entry() previously rebuilt each entry from a fixed field
list that did not include it, so every republish silently dropped it. The regression to guard is
exactly that: audience must survive a republish the same way category/homepage already do.

    scripts/store-index-upsert.test.py
"""
import importlib.util
import json
import sys
import tempfile
from pathlib import Path
from types import SimpleNamespace

spec = importlib.util.spec_from_file_location("upsert", Path(__file__).parent / "store-index-upsert.py")
upsert = importlib.util.module_from_spec(spec)
spec.loader.exec_module(upsert)

failures = []


def check(name, got, expect):
    if got != expect:
        failures.append(f"{name}: expected {expect!r}, got {got!r}")
    else:
        print(f"ok: {name}")


def args(**overrides):
    base = dict(id="plugin", name="Plugin", description=None, author=None)
    base.update(overrides)
    return SimpleNamespace(**base)


# --- unit: build_entry's audience precedence, same shape as the category/homepage tests would be ---

check(
    "a republish with no store.json keeps the existing entry's audience",
    upsert.build_entry({"audience": ["developer"]}, {}, args()).get("audience"),
    ["developer"],
)

check(
    "store.json's audience overrides the existing entry's",
    upsert.build_entry({"audience": ["developer"]}, {"audience": ["accountant"]}, args()).get("audience"),
    ["accountant"],
)

check(
    "a plugin with no audience anywhere gets none (AC-511 criterion 5 fallback stays intact)",
    "audience" in upsert.build_entry(None, {}, args()),
    False,
)

# --- end-to-end: the ticket's actual counter-proof, run through main() against a real index.json ---

with tempfile.TemporaryDirectory() as td:
    index_path = Path(td) / "index.json"
    index_path.write_text(json.dumps({
        "plugins": [{
            "id": "git-status",
            "name": "Git Status",
            "author": "raymondkrahwinkel",
            "audience": ["developer"],
            "latestVersion": "1.5.0",
            "versions": [{"version": "1.5.0", "path": "git-status/git-status-1.5.0.zip"}],
        }],
    }))

    old_argv = sys.argv
    sys.argv = [
        "store-index-upsert.py",
        "--index", str(index_path),
        "--id", "git-status",
        "--name", "Git Status",
        "--author", "raymondkrahwinkel",
        "--version", "1.5.1",
        "--path", "git-status/git-status-1.5.1.zip",
        "--published", "2026-09-08",
    ]
    try:
        rc = upsert.main()
    finally:
        sys.argv = old_argv

    published = json.loads(index_path.read_text())["plugins"][0]
    check("a republish exits OK", rc, upsert.EXIT_OK)
    check("a republish keeps audience on the published entry (the ticket's counter-proof)", published.get("audience"), ["developer"])

    # The other side: a plugin that never had audience must keep parsing, with no key invented for it.
    index_path.write_text(json.dumps({
        "plugins": [{
            "id": "no-audience-plugin",
            "name": "No Audience Plugin",
            "latestVersion": "1.0.0",
            "versions": [{"version": "1.0.0", "path": "x/x-1.0.0.zip"}],
        }],
    }))
    sys.argv = [
        "store-index-upsert.py",
        "--index", str(index_path),
        "--id", "no-audience-plugin",
        "--name", "No Audience Plugin",
        "--version", "1.0.1",
        "--path", "x/x-1.0.1.zip",
        "--published", "2026-09-08",
    ]
    try:
        rc = upsert.main()
    finally:
        sys.argv = old_argv

    published = json.loads(index_path.read_text())["plugins"][0]
    check("a plugin without audience republishes fine", rc, upsert.EXIT_OK)
    check("a plugin without audience does not have one invented", "audience" in published, False)

if failures:
    print(f"\n{len(failures)} test(s) failed:", file=sys.stderr)
    for f in failures:
        print(f"  {f}", file=sys.stderr)
    sys.exit(1)

print("\nall tests passed")

#!/usr/bin/env python3
"""CodeQuality.md §3 gate: /// stays on interface declarations and their members only.

A new /// on an implementation (a class, a record, a struct, an enum, or a non-interface
member) fails the build. This is what stops the AC-319/AC-593 sweep's result from
drifting back to where it started -- the gap the ticket itself names as how the
situation was allowed to happen the first time.

    scripts/check-xmldoc-scope.py [root ...]   # defaults to src and plugins-dev

Cockpit.Plugins.Abstractions is exempt (see its csproj): it ships XML docs to NuGet for
plugin-author IntelliSense, so /// on its public SDK types is not the same kind of doc
this gate is about.
"""
import re
import sys
from pathlib import Path

IFACE_DECL = re.compile(r"^[ \t]*(?:public|internal|file)?[ \t]*(?:partial[ \t]+)?interface[ \t]")
ATTRIBUTE_LINE = re.compile(r"^[ \t]*\[.*\]\s*$")
DOC = re.compile(r"^(?P<indent>[ \t]*)///")


def interface_ranges(lines):
    """(start, end) inclusive line-index pairs covering each interface -- its own
    leading doc/attribute lines through its closing brace, or through the declaration
    line itself for a body-less `interface IFoo;` marker."""
    ranges = []
    i = 0
    while i < len(lines):
        if not IFACE_DECL.match(lines[i]):
            i += 1
            continue
        start = i
        b = start - 1
        while b >= 0 and ATTRIBUTE_LINE.match(lines[b]):
            b -= 1
        true_start = b + 1

        j = i
        found_brace = False
        while j < len(lines):
            if "{" in lines[j]:
                found_brace = True
                break
            if lines[j].rstrip().endswith(";"):
                break
            j += 1

        if found_brace:
            depth = 0
            k = j
            while k < len(lines):
                depth += lines[k].count("{") - lines[k].count("}")
                if depth == 0:
                    break
                k += 1
            ranges.append((true_start, k))
            i = k + 1
        else:
            ranges.append((true_start, j))
            i = j + 1
    return ranges


def in_ranges(idx, ranges):
    return any(s <= idx <= e for s, e in ranges)


def violations_in(path: Path):
    lines = path.read_text(encoding="utf-8").split("\n")
    ranges = interface_ranges(lines)
    found = []
    i = 0
    while i < len(lines):
        m = DOC.match(lines[i])
        if not m:
            i += 1
            continue
        block_start = i
        while i < len(lines) and DOC.match(lines[i]) and DOC.match(lines[i]).group("indent") == m.group("indent"):
            i += 1
        if not in_ranges(block_start, ranges) and not in_ranges(i, ranges):
            found.append(block_start + 1)
    return found


def main():
    roots = [Path(a) for a in sys.argv[1:]] or [Path("src"), Path("plugins-dev")]
    bad = 0
    for root in roots:
        # AC-1257: rglob on a file path yields nothing, which read as a green "no violations".
        if not root.is_dir():
            sys.exit(f"{root}: not a directory -- pass directories to scan, not files")
        for p in sorted(root.rglob("*.cs")):
            if set(p.parts) & {"bin", "obj"}:
                continue
            if "Cockpit.Plugins.Abstractions" in p.parts:
                continue
            for line_no in violations_in(p):
                bad += 1
                print(f"{p}:{line_no}: /// on an implementation, not an interface member")
    if bad:
        print(f"\n{bad} violation(s). Convert with scripts/xmldoc-to-comment.py, or move the /// "
              "if it belongs on an interface member instead.")
        sys.exit(1)
    print("ok: /// appears only on interface declarations and their members.")


if __name__ == "__main__":
    main()

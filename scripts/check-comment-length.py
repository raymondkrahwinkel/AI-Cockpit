#!/usr/bin/env python3
"""CodeQuality.md §5 gate: comment blocks stay within the length limits.

    Check 1: an inline `//` block (`///` excluded) of 4 or more consecutive lines.
    Check 2: an XML `<summary>` block whose own body is longer than 6 lines.

AC-1013 §4: this is the vangnet against new violations slipping in unnoticed. Five
tickets in a row landed 2-6 new-violation files through review despite an explicit
instruction in the agent prompt -- without a build that fails, this debt grows faster
than it is paid down.

Check 2's naive form -- `(^[ \\t]*///[^\\n]*\\n){6,}` -- also matches a `<summary>` and
a `<remarks>` that directly follow each other and are each individually within the
limit, and over-counts badly (125 files on this repo vs. the true 66 at the time this
was measured). CHECK_2 below is anchored to the `<summary>`/`</summary>` tags
themselves so it only ever counts a single tag's own body.

Existing debt (542 files for check 1 at the time this gate was built; 0 for check 2,
already cleaned up under AC-1013) is grandfathered via a per-file violation count in
scripts/comment-length-baseline.txt, so the gate is green on what already exists and
red only on a NEW violation: a file may not carry more violations of a given check
than its baseline row allows. A file with fewer violations than its baseline row
(cleaned up, not yet reflected) does not fail -- it is just a stale baseline row.

Updating the baseline after cleaning up a batch:

    scripts/check-comment-length.py --update-baseline

Regenerates scripts/comment-length-baseline.txt from the current tree, dropping rows
for files that are now clean and shrinking counts for files that improved but still
carry some baselined debt. Review the diff (it should only shrink/remove rows for the
files you touched) and commit it alongside the cleanup.

    scripts/check-comment-length.py [root ...]   # defaults to src and plugins-dev
"""
import re
import sys
from pathlib import Path

BASELINE_PATH = Path(__file__).parent / "comment-length-baseline.txt"

INLINE_BLOCK = re.compile(r"(?:^[ \t]*//(?!/)[^\n]*\n){4,}", re.MULTILINE)
SUMMARY_BLOCK = re.compile(
    r"///[ \t]*<summary>[ \t]*\n(?:[ \t]*///[^\n]*\n){6,}[ \t]*///[ \t]*</summary>",
    re.MULTILINE,
)

CHECKS = {
    "inline": INLINE_BLOCK,
    "summary": SUMMARY_BLOCK,
}


def _line_no(text: str, offset: int) -> int:
    return text.count("\n", 0, offset) + 1


def violations_in(path: Path, text: str | None = None):
    """{check_name: [line_no, ...]} for every block violation in this file."""
    if text is None:
        text = path.read_text(encoding="utf-8")
    found = {}
    for name, pattern in CHECKS.items():
        lines = [_line_no(text, m.start()) for m in pattern.finditer(text)]
        if lines:
            found[name] = lines
    return found


def iter_source_files(roots):
    for root in roots:
        for p in sorted(root.rglob("*.cs")):
            if set(p.parts) & {"bin", "obj"}:
                continue
            yield p


def load_baseline():
    """{(check, path): count}. Missing file or missing row both mean baseline 0."""
    baseline = {}
    if not BASELINE_PATH.exists():
        return baseline
    for line in BASELINE_PATH.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        check, path, count = line.split("\t")
        baseline[(check, path)] = int(count)
    return baseline


def write_baseline(roots):
    rows = []
    for path in iter_source_files(roots):
        found = violations_in(path)
        for check, lines in found.items():
            rows.append((check, path.as_posix(), len(lines)))
    rows.sort()
    with BASELINE_PATH.open("w", encoding="utf-8") as f:
        f.write(
            "# Known comment-length debt, grandfathered by scripts/check-comment-length.py.\n"
            "# Regenerate with: scripts/check-comment-length.py --update-baseline\n"
            "# format: <check>\\t<path>\\t<violation count>\n"
        )
        for check, path, count in rows:
            f.write(f"{check}\t{path}\t{count}\n")
    print(f"wrote {len(rows)} row(s) to {BASELINE_PATH}")


def main():
    args = sys.argv[1:]
    roots = [Path("src"), Path("plugins-dev")]

    if "--update-baseline" in args:
        write_baseline(roots)
        return

    if args:
        roots = [Path(a) for a in args]

    baseline = load_baseline()
    bad = 0
    stale = 0
    for path in iter_source_files(roots):
        found = violations_in(path)
        for check in CHECKS:
            lines = found.get(check, [])
            allowed = baseline.get((check, path.as_posix()), 0)
            excess = len(lines) - allowed
            if excess > 0:
                bad += excess
                for line_no in lines[allowed:]:
                    label = "4+ line inline // block" if check == "inline" else "<summary> longer than 6 lines"
                    print(f"{path}:{line_no}: {label}, over the CodeQuality.md §5 limit")
            elif excess < 0:
                stale += 1

    if bad:
        print(
            f"\n{bad} new comment-length violation(s). Shorten to within the CodeQuality.md §5 limit, "
            "keeping the WHY and the ticket reference -- see AC-1013 for the convention."
        )
        sys.exit(1)

    if stale:
        print(
            f"note: {stale} baseline row(s) allow more violations than the file now has -- "
            "run scripts/check-comment-length.py --update-baseline to shrink them."
        )
    print("ok: no comment-length violations beyond the baseline.")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Test-count gate (AC-1423/AC-1424): the number of test methods per test project only goes down.

Counts the `[Fact]`/`[Theory]` source methods, variants included (`WindowsFact`,
`PosixFact`, `SkippableTheory` ...), per test project under tests/ and plugins-dev/*.Tests,
and compares that with scripts/test-count-baseline.txt.

    Higher than the baseline: red, with the project and the difference.
    Lower than the baseline:  also red, so the drop is pinned -- lower the baseline with it.
    Equal:                    green.

A project with an `allow-growth` rule in the baseline may grow (tests/Cockpit.Journeys: the
convention asks for a journey with every new user route). It still may not shrink unpinned.

    scripts/check-test-count.py                    # check
    scripts/check-test-count.py --update-baseline  # rewrite the counts, keep the rules
"""
import re
import sys
from pathlib import Path

BASELINE_PATH = Path(__file__).parent / "test-count-baseline.txt"
ROOTS = [Path("tests"), Path("plugins-dev")]
TEST_ATTRIBUTE = re.compile(r"^[ \t]*\[\w*(?:Fact|Theory)\b", re.MULTILINE)
HEADER = (
    "# Test methods per test project, pinned by scripts/check-test-count.py.\n"
    "# The count only goes down: lower the row in the same PR that removes tests.\n"
    "# Update with: scripts/check-test-count.py --update-baseline (keeps the allow-growth rules)\n"
    "# format: <project>\t<count>   or   allow-growth\t<project>\n"
)


def project_of(path: Path):
    """tests/<X> for anything under tests/, plugins-dev/<X> only when X is a *.Tests project."""
    root, name = path.parts[0], path.parts[1]
    if root == "plugins-dev" and not name.endswith(".Tests"):
        return None
    return f"{root}/{name}"


def count_tests(roots=ROOTS):
    """{project: number of [Fact]/[Theory] source methods}."""
    counts = {}
    for root in roots:
        for p in sorted(root.rglob("*.cs")):
            if set(p.parts) & {"bin", "obj"}:
                continue
            project = project_of(p)
            if project:
                counts[project] = counts.get(project, 0) + len(
                    TEST_ATTRIBUTE.findall(p.read_text(encoding="utf-8-sig"))
                )
    return counts


def load_baseline(path=BASELINE_PATH):
    """({project: count}, {projects allowed to grow})."""
    counts, growth = {}, set()
    for line in path.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, value = line.split("\t")
        if key == "allow-growth":
            growth.add(value)
        else:
            counts[key] = int(value)
    return counts, growth


def compare(current, baseline, growth):
    """(problems, notes) for the current counts against the baseline."""
    problems, notes = [], []
    for project in sorted(set(current) | set(baseline)):
        now, pinned = current.get(project, 0), baseline.get(project, 0)
        diff = now - pinned
        if diff > 0 and project in growth:
            notes.append(f"{project}: {now} (+{diff}), growth is allowed; the baseline may follow.")
        elif diff > 0:
            problems.append(f"{project}: {now} test methods, baseline {pinned} (+{diff}). Tests only go down.")
        elif diff < 0:
            problems.append(
                f"{project}: {now} test methods, baseline {pinned} ({diff}). "
                "Lower the baseline with it: scripts/check-test-count.py --update-baseline"
            )
    return problems, notes


def write_baseline(current, growth):
    with BASELINE_PATH.open("w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for project in sorted(growth):
            f.write(f"allow-growth\t{project}\n")
        for project, count in sorted(current.items()):
            if count:
                f.write(f"{project}\t{count}\n")
    print(f"wrote {sum(1 for c in current.values() if c)} row(s) to {BASELINE_PATH}")


def main():
    current = count_tests()
    baseline, growth = load_baseline()
    if "--update-baseline" in sys.argv[1:]:
        write_baseline(current, growth)
        return
    problems, notes = compare(current, baseline, growth)
    for line in notes + problems:
        print(line)
    if problems:
        sys.exit(1)
    print(f"ok: {sum(current.values())} test methods in {len(current)} projects, none above or below the baseline.")


if __name__ == "__main__":
    main()

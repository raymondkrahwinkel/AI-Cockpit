#!/usr/bin/env python3
"""Test-count gate (AC-1423/AC-1424, AC-1614, AC-2036): a ceiling of test cases per test project that only goes down.

Counts the test cases per test project under tests/ and plugins-dev/*.Tests: every `[Fact]`/`[Theory]`
method, variants included (`WindowsFact`, `PosixFact`, `SkippableTheory` ...), counts once per
`[InlineData]` row it carries, and once when it has none. The rows in scripts/test-count-baseline.txt
are ceilings, and pinned:

    A project above its ceiling:                     red.
    A ceiling above the one on origin/main:          red. Ceilings only go down.
    A project origin/main has no ceiling for:        its ceiling is at most NEW_PROJECT_CEILING.
    A project in HARD_CEILINGS above that ceiling:   red, unless origin/main already held it at least
                                                     that high (then it may only come down).
    A project below its ceiling:                     red; lower the ceiling with it (--update-baseline),
                                                     so the room cannot be refilled later.

A ceiling goes up only with Raymond's approval, stated in the PR. Which tests may exist at all:
Depot krahwinkel-it/cockpit, Conventions/Testing.md, "Testregels v2".

    scripts/check-test-count.py                    # check
    scripts/check-test-count.py --update-baseline  # lower the ceilings to the current counts; refuses a rise
"""
import re
import subprocess
import sys
from pathlib import Path

BASELINE_PATH = Path(__file__).parent / "test-count-baseline.txt"
BASELINE_ON_MAIN = "origin/main:scripts/test-count-baseline.txt"
ROOTS = [Path("tests"), Path("plugins-dev")]
UNIT = "cases"
NEW_PROJECT_CEILING = 10
# Desktop 10 plus server 10 (AC-1612 §3). One row until the journey files say which product they cover.
HARD_CEILINGS = {"tests/Cockpit.Journeys": 20}
TEST_ATTRIBUTE = re.compile(r"\[\w*(?:Fact|Theory)\b")
INLINE_DATA = re.compile(r"\bInlineData\b")
HEADER = (
    "# Test cases per test project: ceilings, checked by scripts/check-test-count.py against origin/main.\n"
    "# A ceiling only goes down: lower the row in the same PR that removes tests, never raise it.\n"
    "# Update with: scripts/check-test-count.py --update-baseline\n"
    "# format: <project>\t<ceiling>\n"
    f"unit\t{UNIT}\n"
)


def project_of(path: Path):
    """tests/<X> for anything under tests/, plugins-dev/<X> only when X is a *.Tests project."""
    root, name = path.parts[0], path.parts[1]
    if root == "plugins-dev" and not name.endswith(".Tests"):
        return None
    return f"{root}/{name}"


def count_cases(text):
    """Test cases in one source file: per run of attribute lines holding a test attribute, its InlineData rows or 1."""
    # MemberData/ClassData count as one case; their rows only exist at run time.
    cases, block = 0, []
    for line in text.splitlines() + [""]:
        if line.lstrip().startswith("["):
            block.append(line)
            continue
        if block and line.lstrip().startswith("//"):
            continue
        attributes = "\n".join(block)
        if TEST_ATTRIBUTE.search(attributes):
            cases += max(1, len(INLINE_DATA.findall(attributes)))
        block = []
    return cases


def count_tests(roots=ROOTS):
    """{project: number of test cases}."""
    counts = {}
    for root in roots:
        for p in sorted(root.rglob("*.cs")):
            if set(p.parts) & {"bin", "obj"}:
                continue
            project = project_of(p)
            if project:
                counts[project] = counts.get(project, 0) + count_cases(p.read_text(encoding="utf-8-sig"))
    return counts


def parse_baseline(text):
    """{project: ceiling}, or None when the text predates the case ceilings (no `unit cases` row)."""
    rows, unit = {}, None
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, value = line.split("\t")
        if key == "unit":
            unit = value
        elif key != "allow-growth":
            rows[key] = int(value)
    return rows if unit == UNIT else None


def baseline_on_main():
    result = subprocess.run(["git", "show", BASELINE_ON_MAIN], capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        sys.exit(f"cannot read {BASELINE_ON_MAIN} (git fetch origin main first): {result.stderr.strip()}")
    main = parse_baseline(result.stdout)
    if main is None:
        sys.exit(f"{BASELINE_ON_MAIN} has no `unit\t{UNIT}` row: there is nothing to hold the ceilings against.")
    return main


def compare(current, ceilings, main):
    """Problems with the counts against the ceilings, and with the ceilings against those on main."""
    problems = []
    for project in sorted(set(current) | set(ceilings)):
        now, ceiling = current.get(project, 0), ceilings.get(project, 0)
        if now > ceiling:
            problems.append(f"{project}: {now} test cases, ceiling {ceiling} (+{now - ceiling}). Tests only go down.")
        elif now < ceiling:
            problems.append(f"{project}: {now} test cases, ceiling {ceiling}. Lower it: scripts/check-test-count.py --update-baseline")
        on_main = main.get(project)
        if on_main is None and ceiling > NEW_PROJECT_CEILING:
            problems.append(f"{project}: a new project starts at a ceiling of at most {NEW_PROJECT_CEILING}, not {ceiling}.")
        elif on_main is not None and ceiling > on_main:
            problems.append(f"{project}: ceiling {ceiling}, origin/main has {on_main}. A ceiling only goes down.")
        hard = HARD_CEILINGS.get(project)
        if hard is not None and ceiling > hard and (on_main is None or ceiling > on_main):
            problems.append(f"{project}: ceiling {ceiling}, above the hard ceiling of {hard}.")
    return problems


def write_baseline(current):
    with BASELINE_PATH.open("w", encoding="utf-8", newline="\n") as f:
        f.write(HEADER)
        for project, count in sorted(current.items()):
            if count:
                f.write(f"{project}\t{count}\n")
    print(f"wrote {sum(1 for c in current.values() if c)} row(s) to {BASELINE_PATH}")


def main():
    current = count_tests()
    if "--update-baseline" in sys.argv[1:]:
        # With the counts as ceilings only the comparison with main can fail: a rise is refused and nothing is written.
        rises = compare(current, current, baseline_on_main())
        if rises:
            sys.exit("\n".join(rises + ["--update-baseline only lowers ceilings: remove tests instead."]))
        write_baseline(current)
        return
    ceilings = parse_baseline(BASELINE_PATH.read_text(encoding="utf-8"))
    if ceilings is None:
        sys.exit(f"{BASELINE_PATH} has no `unit\t{UNIT}` row: rewrite it with --update-baseline.")
    problems = compare(current, ceilings, baseline_on_main())
    for line in problems:
        print(line)
    if problems:
        sys.exit(1)
    print(f"ok: {sum(current.values())} test cases in {len(current)} projects, all on their ceiling.")


if __name__ == "__main__":
    main()

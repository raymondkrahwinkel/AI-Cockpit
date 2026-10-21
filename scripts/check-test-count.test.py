#!/usr/bin/env python3
"""Tests for scripts/check-test-count.py: the case counting and the ceiling verdicts.

    scripts/check-test-count.test.py
"""
import importlib.util
import os
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-test-count.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, got, expect):
    if got != expect:
        failures.append(f"{name}: expected {expect!r}, got {got!r}")
    else:
        print(f"ok: {name}")


def write(root, rel, text):
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text, encoding="utf-8")


SOURCE = """
    [Fact]
    public void A() {}
    [WindowsFact]
    public void B() {}
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void C(int x) {}
    [SkippableTheory, Trait("a", "b")]
    // why
    [InlineData(3)]
    public void D(int x) {}
    // [Fact] in a comment
    var s = "[Fact]";
"""

here = os.getcwd()
with tempfile.TemporaryDirectory() as tmp:
    os.chdir(tmp)
    write(Path("."), "tests/Cockpit.Core.Tests/Sub/X.cs", SOURCE)
    write(Path("."), "tests/Cockpit.Core.Tests/obj/Skipped.cs", "[Fact]")
    write(Path("."), "plugins-dev/Cockpit.Plugin.Foo.Tests/Y.cs", "[Fact]\nvoid A() {}\n[Fact]\nvoid B() {}")
    write(Path("."), "plugins-dev/Cockpit.Plugin.Foo/Z.cs", "[Fact]")
    counts = gate.count_tests([Path("tests"), Path("plugins-dev")])
    # Fact, WindowsFact, Theory x2 InlineData, SkippableTheory x1 past a comment; not the comment, the string or obj/
    check("counts variants per project", counts, {
        "tests/Cockpit.Core.Tests": 5,
        "plugins-dev/Cockpit.Plugin.Foo.Tests": 2,
    })
    os.chdir(here)  # Windows cannot remove the current directory

main = {"tests/Cockpit.Core.Tests": 10, "tests/Cockpit.Journeys": 25}
ceilings = dict(main)


def problems(current, ceilings=ceilings, main=main):
    return [p.split(":")[0] for p in gate.compare(current, ceilings, main)[0]]


check("on the ceiling is green", problems(dict(main)), [])
check("one more in Core is red", problems({**main, "tests/Cockpit.Core.Tests": 11}), ["tests/Cockpit.Core.Tests"])
check("one less in Core is green", problems({**main, "tests/Cockpit.Core.Tests": 9}), [])
raised = {**main, "tests/Cockpit.Core.Tests": 11}
check("one more with the ceiling raised is red against main", problems(raised, raised), ["tests/Cockpit.Core.Tests"])
check("the same raise before main has case ceilings is green", problems(raised, raised, None), [])
check("Journeys above the hard ceiling, held on main, is green", problems(dict(main)), [])
check("Journeys raised above the hard ceiling is red twice",
      problems({**main, "tests/Cockpit.Journeys": 26}, {**main, "tests/Cockpit.Journeys": 26}),
      ["tests/Cockpit.Journeys", "tests/Cockpit.Journeys"])
new = {**main, "tests/Cockpit.New.Tests": 11}
check("a new project above 10 is red", problems(new, new), ["tests/Cockpit.New.Tests"])
new10 = {**main, "tests/Cockpit.New.Tests": 10}
check("a new project at 10 is green", problems(new10, new10), [])

check("baseline parses", gate.parse_baseline("# c\nunit\tcases\ntests/A\t4\n"), {"tests/A": 4})
check("a method baseline is no case baseline", gate.parse_baseline("allow-growth\ttests/J\ntests/A\t4\n"), None)

if failures:
    print("\n".join(failures))
    raise SystemExit(1)

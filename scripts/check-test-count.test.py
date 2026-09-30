#!/usr/bin/env python3
"""Tests for scripts/check-test-count.py: the counting and the three verdicts (higher, lower, growth).

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
    public void D(int x) {}
    // [Fact] in a comment
    var s = "[Fact]";
"""

here = os.getcwd()
with tempfile.TemporaryDirectory() as tmp:
    os.chdir(tmp)
    write(Path("."), "tests/Cockpit.Core.Tests/Sub/X.cs", SOURCE)
    write(Path("."), "tests/Cockpit.Core.Tests/obj/Skipped.cs", "[Fact]")
    write(Path("."), "plugins-dev/Cockpit.Plugin.Foo.Tests/Y.cs", "[Fact]\n[Fact]")
    write(Path("."), "plugins-dev/Cockpit.Plugin.Foo/Z.cs", "[Fact]")
    counts = gate.count_tests([Path("tests"), Path("plugins-dev")])
    # Fact, WindowsFact, Theory, SkippableTheory; not the comment, the string or obj/
    check("counts variants per project", counts, {
        "tests/Cockpit.Core.Tests": 4,
        "plugins-dev/Cockpit.Plugin.Foo.Tests": 2,
    })
    os.chdir(here)  # Windows cannot remove the current directory

base = {"tests/Cockpit.Core.Tests": 10, "tests/Cockpit.Journeys": 3}
growth = {"tests/Cockpit.Journeys"}


def problems(current):
    return [p.split(":")[0] for p in gate.compare(current, base, growth)[0]]


check("equal is green", problems(dict(base)), [])
check("one more in Core is red", problems({**base, "tests/Cockpit.Core.Tests": 11}), ["tests/Cockpit.Core.Tests"])
check("one less in Core, baseline kept, is red", problems({**base, "tests/Cockpit.Core.Tests": 9}), ["tests/Cockpit.Core.Tests"])
check("one more in Journeys is green", problems({**base, "tests/Cockpit.Journeys": 4}), [])
check("one less in Journeys is red", problems({**base, "tests/Cockpit.Journeys": 2}), ["tests/Cockpit.Journeys"])
check("a new project is red", problems({**base, "tests/Cockpit.New.Tests": 1}), ["tests/Cockpit.New.Tests"])
check("a vanished project is red", problems({"tests/Cockpit.Core.Tests": 10}), ["tests/Cockpit.Journeys"])

with tempfile.TemporaryDirectory() as tmp:
    f = Path(tmp) / "b.txt"
    f.write_text("# c\nallow-growth\ttests/J\ntests/A\t4\n", encoding="utf-8")
    check("baseline parses", gate.load_baseline(f), ({"tests/A": 4}, {"tests/J"}))

if failures:
    print("\n".join(failures))
    raise SystemExit(1)

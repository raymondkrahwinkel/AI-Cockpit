#!/usr/bin/env python3
"""Tests for scripts/check-duplicate-test-bodies.py, against throwaway C# test classes.

The cases that must NOT fire carry the weight here. This guard reads 10k test methods and
a gate that cries wolf over a [Theory]'s InlineData rows, or over a `{` inside a JSON
string literal, is a gate nobody leaves switched on.

    scripts/check-duplicate-test-bodies.test.py
"""
import importlib.util
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-duplicate-test-bodies.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, body, expect_violation):
    source = "public class Cases\n{\n" + body + "\n}\n"
    got = len(gate.duplicates_in(Path("Cases.cs"), source)) > 0
    if got != expect_violation:
        failures.append(f"{name}: expected violation={expect_violation}, got={got}")
    else:
        print(f"ok: {name}")


check("the AC-1244 shape: a copy that kept the neighbour's arrange", """
    [Fact]
    public void CaretBeforeTheAt_DoesNotTrigger() => Assert.Null(Query.From("@foo", 0));

    [Fact]
    public void CaretAtTheVeryStart_ReturnsNull() => Assert.Null(Query.From("@foo", 0));
""", True)

check("a rewritten comment does not hide the copy", """
    [Fact]
    // What the original was for.
    public void First_Case() => Assert.True(Thing.Works());

    [Fact]
    // A different sentence about a case this body never sets up.
    public void Second_Case() => Assert.True(Thing.Works());
""", True)

check("block bodies too", """
    [Fact]
    public void First_Case()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        Assert.Equal("/usr/bin", Build(env)["PATH"]);
    }

    [Fact]
    public void Second_Case()
    {
        var env = new Dictionary<string, string> { ["PATH"] = "/usr/bin" };
        Assert.Equal("/usr/bin", Build(env)["PATH"]);
    }
""", True)

check("different arranges are the normal case", """
    [Fact]
    public void First_Case() => Assert.Null(Query.From("@foo", 0));

    [Fact]
    public void Second_Case() => Assert.Null(Query.From("hi @foo", 2));
""", False)

check("a [Theory] differing only in its InlineData rows", """
    [Theory]
    [InlineData("a")]
    public void First_Case(string s) => Assert.True(Thing.Works(s));

    [Theory]
    [InlineData("b")]
    public void Second_Case(string s) => Assert.True(Thing.Works(s));
""", False)

# Braces inside a raw string literal must not end a body early, or everything after the first
# JSON fixture in a file drifts out of alignment and the comparison is meaningless.
check("a JSON fixture in a raw string literal", '''
    [Fact]
    public void First_Case() => Assert.Equal(Busy, Classify("""{"type":"user"}"""));

    [Fact]
    public void Second_Case() => Assert.Equal(None, Classify("""{"type":"summary"}"""));
''', False)

check("a signature wrapped over two lines still ends at its own semicolon", """
    [Theory]
    [InlineData(1, true)]
    public void First_Case(
        int n, bool expected) =>
        Assert.Equal(expected, Thing.Works(n));

    [Fact]
    public void Second_Case() => Assert.True(Thing.Works(1));
""", False)

check("a helper without a test attribute is not a test", """
    [Fact]
    public void First_Case() => Assert.True(Thing.Works());

    private static void Helper() => Assert.True(Thing.Works());
""", False)

if failures:
    print()
    for f in failures:
        print(f"FAIL: {f}")
    sys.exit(1)
print("\nall 8 cases ok")

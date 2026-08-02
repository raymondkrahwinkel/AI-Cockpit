#!/usr/bin/env python3
"""Tests for scripts/check-xmldoc-scope.py, against a throwaway directory tree rather than
fixed strings -- the guard reads real files, and the two bugs it already caught (a
body-less `interface IFoo;` marker, an attribute line between the doc and the
declaration) were both edge cases in *finding* the interface's own span, not in the
prose comparison.

    scripts/check-xmldoc-scope.test.py
"""
import importlib.util
import sys
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-xmldoc-scope.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, path_text, expect_violations):
    with tempfile.TemporaryDirectory() as td:
        p = Path(td) / "Case.cs"
        p.write_text(path_text, encoding="utf-8")
        got = len(gate.violations_in(p)) > 0
        if got != expect_violations:
            failures.append(f"{name}: expected violations={expect_violations}, got={got}")
        else:
            print(f"ok: {name}")


check(
    "a class's own /// is a violation",
    """namespace N;

/// <summary>Not an interface member.</summary>
public sealed class Foo
{
}
""",
    True,
)

check(
    "an interface's own /// is not a violation",
    """namespace N;

/// <summary>The contract.</summary>
public interface IFoo
{
    /// <summary>A member.</summary>
    void Do();
}
""",
    False,
)

check(
    "a record after an interface, converted to //, is not a violation",
    """namespace N;

/// <summary>The contract.</summary>
public interface IFoo
{
    void Do();
}

// Not an interface member.
public sealed record Bar(int X);
""",
    False,
)

check(
    "a record after an interface, still ///, is a violation",
    """namespace N;

public interface IFoo
{
    void Do();
}

/// <summary>Not an interface member.</summary>
public sealed record Bar(int X);
""",
    True,
)

check(
    "a body-less marker interface's /// is not a violation",
    """namespace N;

/// <summary>Marker interface for services registered with a scoped lifetime.</summary>
public interface IScopedService;
""",
    False,
)

check(
    "an attribute between the doc and the interface line is not a violation",
    """namespace N;

/// <summary>A D-Bus proxy contract.</summary>
[DBusInterface("org.example.Thing")]
public interface IThing
{
    void Do();
}
""",
    False,
)

if failures:
    print(f"\n{len(failures)} test(s) failed:", file=sys.stderr)
    for f in failures:
        print(f"  {f}", file=sys.stderr)
    sys.exit(1)

print("\nall tests passed")

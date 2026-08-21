#!/usr/bin/env python3
"""Tests for scripts/check-descendant-selector-scope.py, against throwaway axaml files.

The cases that matter are the ones that must NOT fail: the guard sits on a form the repo
uses 35 more times legitimately, and a gate that cries wolf gets switched off.

    scripts/check-descendant-selector-scope.test.py
"""
import importlib.util
import sys
import tempfile
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-descendant-selector-scope.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, selector, expect_violation):
    with tempfile.TemporaryDirectory() as td:
        p = Path(td) / "Case.axaml"
        p.write_text(f'<Styles>\n  <Style Selector="{selector}">\n  </Style>\n</Styles>\n', encoding="utf-8")
        got = len(gate.violations_in(p)) > 0
        if got != expect_violation:
            failures.append(f"{name}: expected violation={expect_violation}, got={got}")
        else:
            print(f"ok: {name}")


check("the AC-998 selector", "Border.rowRoot.compact :is(Control).rowIndent", True)
check("child combinator, base type", "Border.tag > :is(Visual)", True)
check("child combinator without spaces", "Border.tag>:is(Control)", True)
check("second half of a comma list", "Border.a TextBlock, Border.b :is(Layoutable)", True)
check("the replacement", "Border.rowRoot.compact Border.rowIndent", False)
check("leaf type on the right", "Button.RowAction :is(TextBlock)", False)
check("concrete type with a namespace prefix", "Border.errorCard materialIcons|MaterialIcon.errorIcon", False)
check("template selector", "ToggleButton.Subtle:checked /template/ ContentPresenter", False)
check("no combinator at all", ":is(Control).rowIndent", False)
check("base type on the left only", ":is(Control).parent TextBlock", False)

# Namespace-prefixed base types are the same rule -- the prefix says where the type lives, not what it is.
check("prefixed base type on the right", "Border.tag :is(avalonia|Control)", True)

if failures:
    print()
    for f in failures:
        print(f"FAIL: {f}")
    sys.exit(1)
print(f"\nall {11} cases ok")

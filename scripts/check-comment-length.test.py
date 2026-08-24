#!/usr/bin/env python3
"""Tests for scripts/check-comment-length.py.

The one regression that matters most here (AC-1013): a `<summary>` directly followed
by a `<remarks>`, each individually within the limit, must NOT be counted as a single
over-length block -- that is exactly the false positive the naive §5 regex produced
(125 files instead of the true 66).

    scripts/check-comment-length.test.py
"""
import importlib.util
import sys
from pathlib import Path

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parent / "check-comment-length.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)

failures = []


def check(name, text, expect):
    """expect: {check_name: violation_count}, omitted checks expected to be absent."""
    got = {k: len(v) for k, v in gate.violations_in(Path("Case.cs"), text).items()}
    if got != expect:
        failures.append(f"{name}: expected {expect}, got {got}")
    else:
        print(f"ok: {name}")


check(
    "a 3-line inline // block is not a violation",
    "// one\n// two\n// three\ncode();\n",
    {},
)

check(
    "a 4-line inline // block is a violation",
    "// one\n// two\n// three\n// four\ncode();\n",
    {"inline": 1},
)

check(
    "/// lines are excluded from the inline check no matter how long",
    "/// one\n/// two\n/// three\n/// four\n/// five\ncode();\n",
    {},
)

check(
    "a <summary> body of 5 lines is not a violation",
    "/// <summary>\n"
    "/// one\n/// two\n/// three\n/// four\n/// five\n"
    "/// </summary>\n",
    {},
)

check(
    "a <summary> body of 6 lines is a violation (matches the ticket's validated regex, "
    "which uses {6,})",
    "/// <summary>\n"
    "/// one\n/// two\n/// three\n/// four\n/// five\n/// six\n"
    "/// </summary>\n",
    {"summary": 1},
)

check(
    "a <summary> and a <remarks>, each within the limit, is not a violation "
    "(the naive regex's false positive)",
    "/// <summary>\n/// one\n/// two\n/// three\n/// </summary>\n"
    "/// <remarks>\n/// four\n/// five\n/// six\n/// </remarks>\n",
    {},
)

check(
    "an inline block and an over-length summary in the same file both count",
    "// one\n// two\n// three\n// four\n"
    "/// <summary>\n"
    "/// a\n/// b\n/// c\n/// d\n/// e\n/// f\n/// g\n"
    "/// </summary>\n",
    {"inline": 1, "summary": 1},
)

# --- baseline grandfathering ---

import tempfile

with tempfile.TemporaryDirectory() as td:
    root = Path(td) / "src"
    root.mkdir()
    baselined = root / "Baselined.cs"
    baselined.write_text("// one\n// two\n// three\n// four\ncode();\n", encoding="utf-8")
    clean = root / "Clean.cs"
    clean.write_text("// one\n// two\ncode();\n", encoding="utf-8")

    original_baseline_path = gate.BASELINE_PATH
    gate.BASELINE_PATH = Path(td) / "baseline.txt"
    gate.BASELINE_PATH.write_text(f"inline\t{baselined.as_posix()}\t1\n", encoding="utf-8")

    baseline = gate.load_baseline()
    got_allowed = baseline.get(("inline", baselined.as_posix()), 0)
    if got_allowed != 1:
        failures.append(f"baseline load: expected allowed=1, got={got_allowed}")
    else:
        print("ok: baseline row grandfathers a known violation")

    got_missing = baseline.get(("inline", clean.as_posix()), 0)
    if got_missing != 0:
        failures.append(f"baseline load: expected 0 for un-baselined file, got={got_missing}")
    else:
        print("ok: a file absent from the baseline defaults to 0 allowed")

    gate.BASELINE_PATH = original_baseline_path

if failures:
    print(f"\n{len(failures)} test(s) failed:", file=sys.stderr)
    for f in failures:
        print(f"  {f}", file=sys.stderr)
    sys.exit(1)

print("\nall tests passed")

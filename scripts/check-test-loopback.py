#!/usr/bin/env python3
"""Reject test listeners that bind outside the loopback interface."""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
FORBIDDEN = (
    re.compile(r"\bIPAddress\s*\.\s*Any\b"),
    re.compile(r"\bListenAnyIP\s*\("),
    re.compile(r"\bnew\s+UdpClient\s*\(\s*0\s*\)"),
    re.compile(r'\bhttps?://(?:0\.0\.0\.0|\*|\+):', re.IGNORECASE),
)


def test_sources() -> list[Path]:
    sources = list((ROOT / "tests").rglob("*.cs"))
    sources.extend(
        source
        for source in (ROOT / "plugins-dev").rglob("*.cs")
        if any(part.endswith(".Tests") for part in source.relative_to(ROOT / "plugins-dev").parts)
    )
    return sorted(sources)


def main() -> int:
    violations: list[str] = []
    for source in test_sources():
        for line_number, line in enumerate(source.read_text(encoding="utf-8").splitlines(), 1):
            code = line.lstrip()
            if code.startswith("//"):
                continue
            if any(pattern.search(code) for pattern in FORBIDDEN):
                violations.append(f"{source.relative_to(ROOT)}:{line_number}: {code.strip()}")

    if violations:
        print("Test listeners must bind to loopback, not every network interface:", file=sys.stderr)
        print("\n".join(violations), file=sys.stderr)
        return 1

    print("Test listener loopback guard passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

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


def allowed_browser_fixture_prefix(source: Path, lines: list[str], index: int) -> bool:
    # AC-2029's DNS-rebinding fixture needs an unresolved Host header on Linux; allow only its opt-in prefix.
    if source.relative_to(ROOT).as_posix() != "plugins-dev/Cockpit.Plugin.Browser.Tests/AgentOwnedTabTests.cs":
        return False
    if lines[index].strip() != '_listener.Prefixes.Add(acceptAnyHost && !OperatingSystem.IsWindows() ? $"http://*:{new Uri(Url).Port}/" : Url);':
        return False
    constructor = "    public OwnedTabFixture(bool acceptAnyHost = false)"
    start = next((i for i, line in enumerate(lines[:index]) if line == constructor), None)
    return start is not None and not any(line.lstrip().startswith("}") for line in lines[start:index])


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
        lines = source.read_text(encoding="utf-8").splitlines()
        for index, line in enumerate(lines):
            code = line.lstrip()
            if code.startswith("//"):
                continue
            if any(pattern.search(code) for pattern in FORBIDDEN) and not allowed_browser_fixture_prefix(source, lines, index):
                violations.append(f"{source.relative_to(ROOT)}:{index + 1}: {code.strip()}")

    if violations:
        print("Test listeners must bind to loopback, not every network interface:", file=sys.stderr)
        print("\n".join(violations), file=sys.stderr)
        return 1

    print("Test listener loopback guard passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

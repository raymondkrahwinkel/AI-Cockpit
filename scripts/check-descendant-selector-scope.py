#!/usr/bin/env python3
"""AC-998 gate: no framework base type on the right of a descendant or child selector.

`Border.rowRoot.compact :is(Control).rowIndent` matched every control in the window,
including template-internal visuals such as Viewbox's container. Those are a visual child
but no logical child, and style detach recurses over the logical children -- so their
StyleClassActivator stayed subscribed to a live ancestor's Classes for the life of the
window. In the 2026-08-21 heap dump that single subscription set held 31.26M objects,
95% of the live heap: ~5,400 discarded transcript rows with everything under them.

Naming a concrete type on the right keeps the selector away from those internals. This is
about the *right-hand* side only -- a class condition on the ancestor is fine, and
`:is(TextBlock)` is fine too: a leaf type, and none of the 12,442 anchors in that dump
was one. Template selectors (`X /template/ Y`) are scoped to their templated parent and
detach with it, so they are not this rule's business.

    scripts/check-descendant-selector-scope.py [root ...]   # defaults to src and plugins-dev
"""
import re
import sys
from pathlib import Path

SELECTOR = re.compile(r"""Selector\s*=\s*(?P<q>["'])(?P<value>.*?)(?P=q)""", re.DOTALL)
IS_MATCH = re.compile(r""":is\(\s*(?:[\w.]+\|)?(?P<type>\w+)\s*\)""")

# Framework base types: every control derives from these, so they also match the internal
# visuals a template builds. Nothing here is a thing you set out to style on its own.
BASE_TYPES = {
    "AvaloniaObject",
    "Control",
    "InputElement",
    "Interactive",
    "Layoutable",
    "StyledElement",
    "TemplatedControl",
    "Visual",
}


def offending_type(selector):
    """The banned base type this one selector ends on, or None if it is fine."""
    tokens = selector.replace(">", " > ").split()
    if len(tokens) < 2 or tokens[-2] == "/template/":
        return None
    m = IS_MATCH.match(tokens[-1])
    return m.group("type") if m and m.group("type") in BASE_TYPES else None


def violations_in(path):
    """(line number, selector, type) for every offending selector in an axaml file."""
    text = path.read_text(encoding="utf-8")
    found = []
    for m in SELECTOR.finditer(text):
        line_no = text.count("\n", 0, m.start()) + 1
        for part in m.group("value").split(","):
            selector = " ".join(part.split())
            bad = offending_type(selector)
            if bad:
                found.append((line_no, selector, bad))
    return found


def main():
    roots = [Path(a) for a in sys.argv[1:]] or [Path("src"), Path("plugins-dev")]
    bad = 0
    for root in roots:
        for p in sorted(root.rglob("*.axaml")):
            if set(p.parts) & {"bin", "obj"}:
                continue
            for line_no, selector, base in violations_in(p):
                bad += 1
                print(f"{p}:{line_no}: descendant selector ends on base type :is({base}) -- {selector}")
    if bad:
        print(f"\n{bad} violation(s). Name the concrete types that carry the class, or put the class "
              "straight on the target element. See AC-998.")
        sys.exit(1)
    print("ok: no descendant selector matches on a framework base type.")


if __name__ == "__main__":
    main()

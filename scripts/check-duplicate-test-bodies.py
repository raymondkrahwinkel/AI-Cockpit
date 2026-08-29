#!/usr/bin/env python3
"""AC-1244 gate: no two tests in one file share a body.

Two [Fact]/[Theory] methods in the same file whose attributes and bodies are identical
once comments are stripped are one of two things, and both need looking at: a plain
duplicate (two names, one test, the second buys nothing), or the AC-1244 gap -- a test
copied to cover a second case where the name and the comment were rewritten and the
arrange never was. The second reads like coverage and is not; it stays green because it
runs the neighbour's scenario under its own name.

Five of five AC-1244 cases were found exactly this way, so the check is deliberately
narrow: byte-equal bodies within one file. It says nothing about whether a name matches
what a body does in general -- that is a judgement, not a check -- only that two tests
that are textually the same test are worth a second look.

Comments are excluded from the comparison because the rewritten comment is the very
thing that hides the copy. Attributes are included, so two [Theory] cases that differ
only in their [InlineData] are not a finding.

    scripts/check-duplicate-test-bodies.py [root ...]   # defaults to tests and plugins-dev
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

DEFAULT_ROOTS = ("tests", "plugins-dev")

ATTRIBUTE_START = re.compile(r"^[ \t]*\[")
TEST_ATTRIBUTE = re.compile(r"^[ \t]*\[[ \t]*(?:Fact|Theory)\b")
METHOD_NAME = re.compile(r"\b(\w+)[ \t]*\(")


def mask(text):
    """A same-length copy of `text` with comment and literal *contents* blanked, plus the
    comment spans. Brace and `;` scanning runs on the mask, so a `{` inside a JSON string
    literal cannot end a method body early; the key is built from the original."""
    out = list(text)
    comments = []
    i, n = 0, len(text)

    def blank(start, end, filler):
        for k in range(start, min(end, n)):
            if out[k] != "\n":
                out[k] = filler

    while i < n:
        if text.startswith("//", i):
            end = text.find("\n", i)
            end = n if end < 0 else end
            comments.append((i, end))
            blank(i, end, " ")
            i = end
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            comments.append((i, end))
            blank(i, end, " ")
            i = end
        elif text.startswith('"""', i):
            quotes = 0
            while i + quotes < n and text[i + quotes] == '"':
                quotes += 1
            fence = '"' * quotes
            end = text.find(fence, i + quotes)
            end = n if end < 0 else end + quotes
            blank(i + quotes, end - quotes, "x")
            i = end
        elif text.startswith('@"', i):
            j = i + 2
            while j < n:
                if text[j] == '"':
                    if j + 1 < n and text[j + 1] == '"':
                        j += 2
                        continue
                    break
                j += 1
            blank(i + 2, j, "x")
            i = j + 1
        elif text[i] in '"\'':
            quote, j = text[i], i + 1
            while j < n and text[j] != quote and text[j] != "\n":
                j += 2 if text[j] == "\\" else 1
            blank(i + 1, j, "x")
            i = j + 1
        else:
            i += 1

    return "".join(out), comments


def _normalise(text, comments, start, end):
    """The slice [start, end) with any comment inside it removed and whitespace collapsed."""
    kept, cursor = [], start
    for c_start, c_end in comments:
        if c_end <= start or c_start >= end:
            continue
        kept.append(text[cursor:max(cursor, c_start)])
        cursor = max(cursor, c_end)
    kept.append(text[cursor:end])
    return " ".join("".join(kept).split())


def tests_in(text):
    """(name, line_no, key) for every [Fact]/[Theory] method in this file."""
    masked, comments = mask(text)
    lines = masked.split("\n")
    starts, offset = [], 0
    for line in lines:
        starts.append(offset)
        offset += len(line) + 1

    found, i = [], 0
    while i < len(lines):
        if not ATTRIBUTE_START.match(lines[i]):
            i += 1
            continue

        # A comment between two attributes is a blank line in the mask; stepping over it keeps the run whole,
        # and `last_attribute` hands back any blank lines that turned out not to be inside one.
        run_start = last_attribute = i
        while i < len(lines):
            if ATTRIBUTE_START.match(lines[i]):
                i += 1
                last_attribute = i
            elif not lines[i].strip():
                i += 1
            else:
                break
        i = last_attribute
        if not any(TEST_ATTRIBUTE.match(lines[k]) for k in range(run_start, i)):
            continue

        # The signature runs from here to whichever comes first: the body's `{` or an `=>`.
        j = i
        while j < len(lines) and "{" not in lines[j] and "=>" not in lines[j]:
            j += 1
        if j >= len(lines):
            break

        name = METHOD_NAME.search(" ".join(lines[i:j + 1]))
        if "{" in lines[j] and lines[j].index("{") < (lines[j] + "=>").index("=>"):
            body = starts[j] + lines[j].index("{")
            end = _end_of_block(masked, body)
        else:
            # From the `=>` rather than the line start: a signature wrapped over two lines puts its closing
            # `)` before the arrow, and counting that would leave the depth negative for the rest of the file.
            body = starts[j] + lines[j].index("=>")
            end = _end_of_statement(masked, body)

        if end is not None and name is not None:
            # The signature is left out of the key on purpose: the method name is exactly what these two
            # tests do not share, and the parameter names they do use show up in the body anyway.
            key = (_normalise(text, comments, starts[run_start], starts[i])
                   + " | " + _normalise(text, comments, body, end))
            found.append((name.group(1), i + 1, key))
            i = masked.count("\n", 0, end) + 1
        else:
            i = j + 1

    return found


def _end_of_block(masked, brace):
    depth = 0
    for k in range(brace, len(masked)):
        if masked[k] == "{":
            depth += 1
        elif masked[k] == "}":
            depth -= 1
            if depth == 0:
                return k + 1
    return None


def _end_of_statement(masked, start):
    depth = 0
    for k in range(start, len(masked)):
        if masked[k] in "([{":
            depth += 1
        elif masked[k] in ")]}":
            depth -= 1
        elif masked[k] == ";" and depth == 0:
            return k + 1
    return None


def duplicates_in(path, text=None):
    """[(key, [(name, line), ...]), ...] for every group of same-bodied tests in this file."""
    if text is None:
        text = path.read_text(encoding="utf-8", errors="replace")
    by_key = defaultdict(list)
    for name, line, key in tests_in(text):
        by_key[key].append((name, line))
    return [(key, hits) for key, hits in by_key.items() if len(hits) > 1]


def main(argv):
    roots = [Path(a) for a in argv[1:]] or [Path(r) for r in DEFAULT_ROOTS]
    failures = 0
    for root in roots:
        for path in sorted(root.rglob("*.cs")):
            for _, hits in duplicates_in(path):
                failures += 1
                names = ", ".join(f"{name} (line {line})" for name, line in hits)
                print(f"{path}: same body under {len(hits)} names -- {names}")

    if failures:
        print(
            f"\n{failures} group(s) of tests share a body within one file. Either the copy never got the "
            "arrange its name promises (AC-1244), or one of them is redundant and can go.",
            file=sys.stderr,
        )
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))

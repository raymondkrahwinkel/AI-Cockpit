#!/usr/bin/env python3
"""Rewrites XML doc comments (///) on implementations to plain comments (//).

CodeQuality.md §3 keeps /// for interface methods only. The rule is about the marker,
not the explanation: every word of prose survives the rewrite. XML markup is translated
to the idiom the plain // comments in this repo already use — `code`, *emphasis*,
"  - " bullets — because leaving a literal <summary>/<para> inside a // comment reads
worse than the doc it replaced.

    scripts/xmldoc-to-comment.py src/Cockpit.App/Services          # rewrite
    scripts/xmldoc-to-comment.py --check src/Cockpit.App/Services  # prove nothing was lost

--check re-reads the pre-rewrite files from git HEAD and compares the two comment word
bags. It fails on a word gained, a word lost, or a /// left behind, so the rewrite is
verifiable per round instead of on a reviewer's spot-check of a four-figure diff.

Files that declare an interface are skipped and listed: whether a /// in them belongs to
an interface member or to something else in the same file is a judgement, not a regex.
"""
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path

DOC = re.compile(r"^(?P<indent>[ \t]*)///(?P<body>.*)$")
DECLARES_INTERFACE = re.compile(r"^[ \t]*(?:public|internal|file)?[ \t]*(?:partial[ \t]+)?interface[ \t]",
                                re.M)

PAIRED = [
    (re.compile(r"<c>(.*?)</c>", re.S), r"`\1`"),
    (re.compile(r"<b>(.*?)</b>", re.S), r"*\1*"),
    (re.compile(r"<em>(.*?)</em>", re.S), r"*\1*"),
    (re.compile(r"<i>(.*?)</i>", re.S), r"*\1*"),
]
SELF = [
    (re.compile(r'<see\s+cref="([^"]*)"\s*/>'), r"`\1`"),
    (re.compile(r'<see\s+langword="([^"]*)"\s*/>'), r"`\1`"),
    (re.compile(r'<seealso\s+cref="([^"]*)"\s*/>'), r"`\1`"),
    (re.compile(r'<paramref\s+name="([^"]*)"\s*/>'), r"`\1`"),
    (re.compile(r'<typeparamref\s+name="([^"]*)"\s*/>'), r"`\1`"),
]
PARAM_OPEN = re.compile(r'<param\s+name="([^"]*)"\s*>')
DROP_TAGS = re.compile(r"</?(summary|remarks|list|param|typeparam|returns|value)\b[^>]*>")
PARA = re.compile(r"<para\s*>")
PARA_CLOSE = re.compile(r"</para\s*>")
INHERITDOC = re.compile(r"<inheritdoc\s*/?>")
ITEM_OPEN = re.compile(r"<item\s*>")
ITEM_CLOSE = re.compile(r"</item\s*>")

PARA_MARK = "\x00PARA\x00"
PARAM_MARK = "\x00PARAM\x00"


def convert_block(bodies):
    """bodies: the text after '///' for each line of one contiguous doc block."""
    text = "\n".join(bodies)

    # An <inheritdoc/> is a pointer, not prose: as "// <inheritdoc/>" it says nothing and
    # no longer inherits anything either, so the line goes.
    text = INHERITDOC.sub("", text)
    for rx, rep in PAIRED:
        text = rx.sub(rep, text)
    for rx, rep in SELF:
        text = rx.sub(rep, text)
    text = PARAM_OPEN.sub(lambda m: "%s`%s`: " % (PARAM_MARK, m.group(1)), text)
    text = ITEM_OPEN.sub("- ", text)
    text = ITEM_CLOSE.sub("", text)
    text = PARA.sub(PARA_MARK, text)
    text = PARA_CLOSE.sub("", text)
    text = DROP_TAGS.sub("", text)

    out = []
    seen_param = False
    for line in text.split("\n"):
        # A <para> breaks the paragraph; the first <param> is where the summary ends.
        blank_before = PARA_MARK in line or (PARAM_MARK in line and not seen_param)
        if PARAM_MARK in line:
            seen_param = True
        line = line.replace(PARA_MARK, "").replace(PARAM_MARK, "").rstrip()
        if blank_before and out and out[-1] != "":
            out.append("")
        if line.strip() == "":
            continue
        out.append(line)

    while out and out[0] == "":
        out.pop(0)
    while out and out[-1] == "":
        out.pop()
    return out


def convert_file(path: Path) -> bool:
    lines = path.read_text(encoding="utf-8").split("\n")
    out = []
    i = 0
    changed = False
    while i < len(lines):
        m = DOC.match(lines[i])
        if not m:
            out.append(lines[i])
            i += 1
            continue
        # One block = the run of /// lines sharing an indent, so a doc and the member it
        # sits on are rewritten together.
        indent = m.group("indent")
        bodies = []
        while i < len(lines):
            m2 = DOC.match(lines[i])
            if not m2 or m2.group("indent") != indent:
                break
            body = m2.group("body")
            bodies.append(body[1:] if body.startswith(" ") else body)
            i += 1
        for line in convert_block(bodies):
            out.append((indent + "// " + line).rstrip() if line else indent + "//")
        changed = True
    if changed:
        path.write_text("\n".join(out), encoding="utf-8")
    return changed


# --- check ------------------------------------------------------------------
STRIP_MARKUP = re.compile(r"</?[a-zA-Z][^>]*>")
ATTR = re.compile(r'<(?:see|seealso)\s+(?:cref|langword|href)="([^"]*)"\s*/>|'
                  r'<(?:param|paramref|typeparam|typeparamref)\s+name="([^"]*)"\s*/?>')
PUNCT_ONLY = re.compile(r"^[^\w]+$")


def prose(text, marker):
    """Every word of comment prose, with markup, marker and layout removed.

    A cref/name attribute carries real content (the identifier the sentence points at),
    so it is expanded to its value before the tags are stripped — otherwise the check
    would report every identifier the rewrite unwrapped as a word gained.
    """
    kept = []
    for line in text.split("\n"):
        s = line.strip()
        if marker == "///":
            if not s.startswith("///"):
                continue
            s = s[3:]
        else:
            if not s.startswith("//") or s.startswith("///"):
                continue
            s = s[2:]
        kept.append(s)
    # Join before stripping: a <see cref=.../> may span two source lines.
    text = ATTR.sub(lambda m: " %s " % (m.group(1) or m.group(2)), "\n".join(kept))
    text = STRIP_MARKUP.sub(" ", text)
    text = text.replace("`", " ").replace("*", " ")
    # Bare punctuation is layout, not prose: the rewrite adds ": " after a param name and
    # "- " for a list item, and drops the "/>" of a self-closing tag.
    return [w for w in text.split() if not PUNCT_ONLY.match(w)]


def check_file(path: Path) -> list[str]:
    old = subprocess.run(["git", "show", "HEAD:./" + path.name],
                         cwd=path.parent, capture_output=True, text=True,
                         encoding="utf-8").stdout
    new = path.read_text(encoding="utf-8")
    # What was in /// must now be in //, on top of the // comments that were already there.
    was = Counter(prose(old, "///")) + Counter(prose(old, "//"))
    now = Counter(prose(new, "//"))
    problems = []
    if was - now:
        problems.append("lost words: %s" % dict(list((was - now).items())[:8]))
    if now - was:
        problems.append("new words:  %s" % dict(list((now - was).items())[:8]))
    left = sum(1 for l in new.split("\n") if l.strip().startswith("///"))
    if left:
        problems.append("/// left:   %d lines" % left)
    return problems


def main():
    args = [a for a in sys.argv[1:] if a != "--check"]
    check = "--check" in sys.argv[1:]
    if not args:
        sys.exit(__doc__)
    root = Path(args[0])

    targets, skipped = [], []
    for p in sorted(root.rglob("*.cs")):
        (skipped if DECLARES_INTERFACE.search(p.read_text(encoding="utf-8")) else targets).append(p)
    for p in skipped:
        print("skipped (declares an interface, decide by hand): %s" % p)

    if not check:
        n = sum(1 for p in targets if convert_file(p))
        print("rewrote %d/%d files" % (n, len(targets)))
        return

    bad = 0
    for p in targets:
        problems = check_file(p)
        if problems:
            bad += 1
            print("FAIL %s" % p)
            for line in problems:
                print("   " + line)
    if bad:
        sys.exit("%d file(s) lost or gained comment prose" % bad)
    print("OK: %d files, no comment prose lost or added, no /// left" % len(targets))


if __name__ == "__main__":
    main()

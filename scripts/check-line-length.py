#!/usr/bin/env python3
"""Fails when a change adds a line of more than 120 columns to a C# file or to a document in docs/.

Only lines that the change adds are checked, measured against the merge base with the base branch
(origin/main by default). Long lines that are already there, and long lines that the change only moves,
are left alone.

    python scripts/check-line-length.py                  # the working tree against origin/main
    python scripts/check-line-length.py --base origin/x  # against another base
    python scripts/check-line-length.py --selftest       # prove the check still catches a long line

A line that cannot be wrapped is let through in two cases: it holds a URL (http:// or https://), or it is
a row of a Markdown table (it starts with "|"). A long line that the change removes from one checked file
and adds to another counts as moved: each removal lets one addition of the same text through.
The length is counted in characters.
"""
import argparse
import collections
import re
import subprocess
import sys

LIMIT = 120
HUNK = re.compile(r"^@@ -\d+(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")


def is_checked(path):
    """C# anywhere, Markdown directly in docs/."""
    lower = path.lower()
    if lower.endswith(".cs"):
        return True
    return path.startswith("docs/") and lower.endswith(".md") and "/" not in path[len("docs/"):]


def may_be_long(path, text):
    if "http://" in text or "https://" in text:
        return True
    return path.lower().endswith(".md") and text.lstrip().startswith("|")


def header_path(line, prefix):
    """The path in a '--- a/x' or '+++ b/x' header line, or None for /dev/null."""
    target = line[4:].rstrip("\t")
    if len(target) > 1 and target.startswith('"') and target.endswith('"'):
        target = target[1:-1]
    return target[len(prefix):] if target.startswith(prefix) else None


def find_long_lines(diff_text):
    """Returns (path, line number, length) for each added line over the limit in a unified diff.

    The line counts in each hunk header decide what is content, so a content line that looks like a file
    header ("++ x" added, "-- x" removed) is read as content.
    """
    moved_away = collections.Counter()
    added = []
    old_path = new_path = None
    line_no = old_left = new_left = 0
    for line in diff_text.split("\n"):
        if old_left > 0 or new_left > 0:
            text = line[1:].rstrip("\r")
            if line.startswith("-"):
                old_left -= 1
                if old_path is not None and len(text) > LIMIT and is_checked(old_path):
                    moved_away[text.strip()] += 1
            elif line.startswith("+"):
                new_left -= 1
                if new_path is not None:
                    added.append((new_path, line_no, text))
                line_no += 1
            elif line.startswith(" "):
                old_left -= 1
                new_left -= 1
                line_no += 1
            continue
        hunk = HUNK.match(line)
        if hunk:
            old_left = int(hunk.group(1) or 1)
            line_no = int(hunk.group(2))
            new_left = int(hunk.group(3) or 1)
        elif line.startswith("diff --git "):
            old_path = new_path = None
        elif line.startswith("--- "):
            old_path = header_path(line, "a/")
        elif line.startswith("+++ "):
            new_path = header_path(line, "b/")
    found = []
    for path, line_no, text in added:
        if len(text) <= LIMIT or not is_checked(path) or may_be_long(path, text):
            continue
        if moved_away[text.strip()] > 0:
            moved_away[text.strip()] -= 1
            continue
        found.append((path, line_no, len(text)))
    return found


def git(*args):
    result = subprocess.run(
        ["git", "-c", "core.quotepath=off", *args],
        capture_output=True, encoding="utf-8", errors="replace")
    if result.returncode != 0:
        sys.exit(f"check-line-length: git {' '.join(args)} failed: {result.stderr.strip()}")
    return result.stdout


def selftest():
    long_line = "x" * 121

    def file(path, hunk, *lines):
        return [f"diff --git a/{path} b/{path}", f"--- a/{path}", f"+++ b/{path}", hunk, *lines]

    diff = "\n".join(
        file("src/A.cs", "@@ -3,0 +4,3 @@",
             "+" + long_line,                         # line 4: 121 columns
             "+" + "y" * 120,                         # line 5: exactly the limit
             "+// https://example.org/" + long_line)  # line 6: holds a URL
        + file("src/B.cs", "@@ -10,2 +9,0 @@",
               "-    " + "m" * 120,                   # a long line that leaves B.cs ...
               "-" + "s" * 30)                        # ... and a short one
        + file("tests/Deep/C.cs", "@@ -6,1 +6,5 @@",
               " context",                            # line 6: unchanged
               "+  " + "m" * 120,                     # line 7: arrives from B.cs, moved, not new
               "+  " + "m" * 120,                     # line 8: the same text once more, this one is new
               "++ " + long_line,                     # line 9: content that looks like a file header
               "+| " + long_line + " |")              # line 10: a table row, but not in Markdown
        + file("src/With Space.cs\t", "@@ -0,0 +2 @@",
               "+" + long_line)                       # a path with a space
        + file("docs/Overview.md", "@@ -0,0 +2,2 @@",
               "+| " + long_line + " |",              # line 2: a table row
               "+" + long_line)                       # line 3: over the limit
        + file("README.md", "@@ -4 +4 @@",
               "-" + "z" * 130,                       # leaves a file that is not checked ...
               "+" + long_line)                       # not a checked file
        + file("src/D.cs", "@@ -0,0 +1 @@",
               "+" + "z" * 130)                       # ... so this is new here
        + file("docs/sub/Deep.md", "@@ -0,0 +1 @@",
               "+" + long_line))                      # not directly in docs/
    expected = [
        ("src/A.cs", 4, 121), ("tests/Deep/C.cs", 8, 122), ("tests/Deep/C.cs", 9, 123),
        ("tests/Deep/C.cs", 10, 125), ("src/With Space.cs", 2, 121), ("docs/Overview.md", 3, 121),
        ("src/D.cs", 1, 130)]
    actual = find_long_lines(diff)
    if actual != expected:
        sys.exit(f"check-line-length: selftest FAILED\n  expected {expected}\n  got      {actual}")
    if find_long_lines("\n".join(file("src/A.cs", "@@ -1 +1 @@", "-a", "+b"))):
        sys.exit("check-line-length: selftest FAILED: a short line was reported")
    print("check-line-length: selftest passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="origin/main", help="the branch the change will merge into")
    parser.add_argument("--selftest", action="store_true", help="check the check itself and stop")
    args = parser.parse_args()
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(errors="replace")
    if args.selftest:
        selftest()
        return
    merge_base = git("merge-base", args.base, "HEAD").strip()
    found = find_long_lines(git(
        "diff", "--unified=0", "--no-color", "--no-ext-diff", "--src-prefix=a/", "--dst-prefix=b/", merge_base))
    for path, line_no, length in found:
        print(f"{path}:{line_no}: {length} columns, the limit is {LIMIT}")
    if found:
        sys.exit(
            f"check-line-length: {len(found)} added line(s) over {LIMIT} columns. Wrap the line; do not shorten "
            "names to make it fit. A line with a URL and a Markdown table row are let through.")
    print(f"check-line-length: no added line over {LIMIT} columns")


if __name__ == "__main__":
    main()

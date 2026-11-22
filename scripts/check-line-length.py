#!/usr/bin/env python3
"""Fails when a change adds a line of more than 120 columns to a C# file or to a document in docs/.

Only lines that the change adds are checked, measured against the merge base with the base branch
(origin/main by default). Long lines that are already there, and long lines that the change only moves,
are left alone.

    python scripts/check-line-length.py                  # the working tree against origin/main
    python scripts/check-line-length.py --base origin/x  # against another base
    python scripts/check-line-length.py --selftest       # prove the check still catches a long line

A line that cannot be wrapped is let through in two cases: it holds a URL (http:// or https://), or it is
a row of a Markdown table (it starts with "|").
"""
import argparse
import re
import subprocess
import sys

LIMIT = 120
HUNK = re.compile(r"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")


def is_checked(path):
    """C# anywhere, Markdown directly in docs/."""
    if path.endswith(".cs"):
        return True
    return path.startswith("docs/") and path.endswith(".md") and "/" not in path[len("docs/"):]


def may_be_long(path, text):
    if "http://" in text or "https://" in text:
        return True
    return path.endswith(".md") and text.lstrip().startswith("|")


def find_long_lines(diff_text):
    """Returns (path, line number, length) for each added line over the limit in a unified diff."""
    removed = set()
    added = []
    path = None
    line_no = 0
    for line in diff_text.splitlines():
        if line.startswith("+++ "):
            target = line[4:].rstrip("\t")
            path = target[2:] if target.startswith("b/") else None
            continue
        if line.startswith("--- "):
            continue
        hunk = HUNK.match(line)
        if hunk:
            line_no = int(hunk.group(1))
            continue
        if line.startswith("-"):
            removed.add(line[1:].strip())
        elif line.startswith("+"):
            if path is not None:
                added.append((path, line_no, line[1:]))
            line_no += 1
        elif line.startswith(" "):
            line_no += 1
    found = []
    for path, line_no, text in added:
        if len(text) <= LIMIT or not is_checked(path):
            continue
        if may_be_long(path, text) or text.strip() in removed:
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
    long_line = "x" * (LIMIT + 1)
    diff = "\n".join([
        "--- a/src/A.cs", "+++ b/src/A.cs", "@@ -3,0 +4,3 @@",
        "+" + long_line,                    # line 4: over the limit
        "+" + "y" * LIMIT,                  # line 5: exactly the limit
        "+// https://example.org/" + long_line,  # line 6: holds a URL
        "--- a/src/B.cs", "+++ b/src/B.cs", "@@ -10 +0,0 @@",
        "-    " + "m" * LIMIT,              # a long line that leaves B.cs ...
        "--- a/src/C.cs", "+++ b/src/C.cs", "@@ -0,0 +7 @@",
        "+  " + "m" * LIMIT,                # ... and arrives in C.cs: moved, not new
        "--- a/docs/Overview.md", "+++ b/docs/Overview.md", "@@ -0,0 +2,2 @@",
        "+| " + long_line + " |",           # line 2: a table row
        "+" + long_line,                    # line 3: over the limit
        "--- a/README.md", "+++ b/README.md", "@@ -0,0 +1 @@",
        "+" + long_line,                    # not a checked file
        "--- a/docs/sub/Deep.md", "+++ b/docs/sub/Deep.md", "@@ -0,0 +1 @@",
        "+" + long_line,                    # not directly in docs/
    ])
    expected = [("src/A.cs", 4, LIMIT + 1), ("docs/Overview.md", 3, LIMIT + 1)]
    actual = find_long_lines(diff)
    if actual != expected:
        sys.exit(f"check-line-length: selftest FAILED\n  expected {expected}\n  got      {actual}")
    if find_long_lines("--- a/src/A.cs\n+++ b/src/A.cs\n@@ -1 +1 @@\n-a\n+b\n"):
        sys.exit("check-line-length: selftest FAILED: a short line was reported")
    print("check-line-length: selftest passed")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--base", default="origin/main", help="the branch the change will merge into")
    parser.add_argument("--selftest", action="store_true", help="check the check itself and stop")
    args = parser.parse_args()
    if args.selftest:
        selftest()
        return
    merge_base = git("merge-base", args.base, "HEAD").strip()
    found = find_long_lines(git("diff", "--unified=0", "--no-color", "--no-ext-diff", merge_base))
    for path, line_no, length in found:
        print(f"{path}:{line_no}: {length} columns, the limit is {LIMIT}")
    if found:
        sys.exit(
            f"check-line-length: {len(found)} added line(s) over {LIMIT} columns. Wrap the line; do not shorten "
            "names to make it fit. A line with a URL and a Markdown table row are let through.")
    print(f"check-line-length: no added line over {LIMIT} columns")


if __name__ == "__main__":
    main()

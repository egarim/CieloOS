#!/usr/bin/env python3
"""Hand a task to DeepSeek, get files back, leave them in the working tree.

    python tools/delegate/deepseek.py --brief docs/briefs/01-foo.md \
        --context 'docs/tls-and-proxies.md' 'src/backend/**/*.cs' --write

The division of labour this exists to serve: DeepSeek does the task, codex
reviews it. That only works if the task arrives with enough context to be done
properly and the result lands somewhere a review can see it — a diff in a branch,
not a paste in a chat window.

Deliberate choices:

* **deepseek-chat, pinned.** The benchmark this project runs compares agents with
  the model held constant; a harness that silently upgraded itself would make
  every past number meaningless. --model exists, but the default does not drift.

* **Files come back between markers, not as JSON.** The first version asked for
  JSON, which worked for a two-paragraph smoke test and failed on the first real
  task: the model returned raw C#, and it was right to. Putting a thousand lines
  of source inside a JSON string means escaping every quote, backslash and
  newline in it, which spends output tokens on punctuation and turns one bad
  escape into an unparseable reply with the work already paid for. Markers cost
  nothing and cannot be escaped wrong.

* **It writes into the working tree and commits nothing.** The whole point is
  that a human or a reviewer sees a diff before anything is permanent. --write is
  opt-in; without it you get a manifest and nothing touches disk.

* **Refuses to write outside the repo.** A path traversing out of the tree is a
  model mistake at best, so it is a hard error rather than a warning.

* **The key is read from the environment and never printed**, not in errors, not
  in the transcript it saves.
"""
import argparse
import glob
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request

API = "https://api.deepseek.com/chat/completions"
DEFAULT_MODEL = "deepseek-chat"

SYSTEM = """You are writing production code for CieloOS, an operating system \
operated by AI agents through a policy-checked command bus. The repository has a \
strong and unusual house style. Follow it exactly.

COMMENTS. Comments explain WHY, never what. A comment that restates the code is \
worse than no comment. Where a decision was made against an obvious alternative, \
say which alternative and why it lost. Where a bug caused the code to exist, say \
what the bug did. Never write decorative banners, never write "TODO", and never \
use emoji.

TESTS. Every behavioural change needs a test that would fail without it. Prefer a \
test that demonstrates the bug to one that asserts the fix.

ERRORS. A message is for the person reading it. Say what happened and what to do. \
Never print a stack trace as a user-facing error.

SCOPE. Do exactly the task. Do not reformat untouched code, do not rename things \
that were not asked about, do not add abstractions for hypothetical futures.

OUTPUT FORMAT. Reply in exactly this shape and nothing else. Do not wrap it in \
markdown fences. Do not add commentary outside the sections.

===NOTES===
What you did and why, in prose.
===CONCERNS===
- anything you believe is wrong with the task as specified, one per line
===FOLLOWUPS===
- work this task implies but deliberately leaves undone, one per line
===FILE path/relative/to/repo/root.cs===
the entire file content, verbatim, with no escaping of any kind
===END FILE===
===FILE another/file.cs===
...
===END FILE===

Repeat FILE blocks as needed. Every file must be COMPLETE: the content you give \
REPLACES the file entirely, so never emit a fragment, a diff, or an ellipsis \
standing in for unchanged code. If a file is too large to reproduce in full, say \
so under CONCERNS and omit it rather than truncating it."""

FILE_OPEN = re.compile(r"^===FILE\s+(.+?)===\s*$", re.MULTILINE)
FILE_CLOSE = "===END FILE==="


def read_key():
    key = os.environ.get("DEEPSEEK_API_KEY", "")
    if not key:
        sys.exit("DEEPSEEK_API_KEY is not set. Export it and try again; it is never read from a file.")
    return key


def gather(patterns, root, budget):
    """Collect context files, stopping at the character budget.

    Silently dropping context is how a delegation quietly gets worse, so what did
    not fit is reported rather than swallowed.
    """
    chunks, used, skipped = [], 0, []
    for pattern in patterns:
        matches = sorted(glob.glob(os.path.join(root, pattern), recursive=True))
        if not matches:
            skipped.append("%s (matched nothing)" % pattern)
        for path in matches:
            if not os.path.isfile(path):
                continue
            rel = os.path.relpath(path, root).replace(os.sep, "/")
            try:
                text = open(path, encoding="utf-8", errors="replace").read()
            except OSError as exc:
                skipped.append("%s (%s)" % (rel, exc))
                continue
            if used + len(text) > budget:
                skipped.append("%s (%d chars, over budget)" % (rel, len(text)))
                continue
            chunks.append("----- FILE: %s -----\n%s" % (rel, text))
            used += len(text)
    return chunks, used, skipped


def call(key, model, messages, max_tokens, retries=3):
    body = json.dumps({
        "model": model,
        "messages": messages,
        "temperature": 0,
        "max_tokens": max_tokens,
    }).encode()
    last = None
    for attempt in range(1, retries + 1):
        req = urllib.request.Request(
            API, data=body,
            headers={"Content-Type": "application/json", "Authorization": "Bearer " + key},
        )
        try:
            with urllib.request.urlopen(req, timeout=900) as response:
                return json.load(response)
        except urllib.error.HTTPError as exc:
            detail = exc.read().decode("utf-8", "replace")[:400]
            last = "HTTP %s: %s" % (exc.code, detail)
            if exc.code not in (429, 500, 502, 503, 504):
                break
        except Exception as exc:
            last = str(exc)
        if attempt < retries:
            wait = 5 * attempt
            print("  attempt %d failed (%s); retrying in %ds" % (attempt, last, wait), file=sys.stderr)
            time.sleep(wait)
    sys.exit("DeepSeek call failed: %s" % last)


def parse(raw):
    """Split the reply into prose sections and complete files."""
    sections = {"NOTES": "", "CONCERNS": "", "FOLLOWUPS": ""}
    for name in sections:
        match = re.search(r"^===%s===\s*$(.*?)(?=^===|\Z)" % name, raw, re.MULTILINE | re.DOTALL)
        if match:
            sections[name] = match.group(1).strip()

    files, unterminated = [], []
    for match in FILE_OPEN.finditer(raw):
        path = match.group(1).strip()
        start = match.end()
        end = raw.find(FILE_CLOSE, start)
        if end < 0:
            # Almost always the token ceiling. Writing a half file would be worse
            # than writing none, so it is reported and dropped.
            unterminated.append(path)
            continue
        files.append((path, raw[start:end].lstrip("\n").rstrip() + "\n"))
    return sections, files, unterminated


def main():
    parser = argparse.ArgumentParser(description="Delegate a coding task to DeepSeek.")
    parser.add_argument("--brief", required=True, help="markdown file describing the task")
    parser.add_argument("--context", nargs="*", default=[], help="repo-relative globs to include")
    parser.add_argument("--model", default=DEFAULT_MODEL)
    parser.add_argument("--max-tokens", type=int, default=8000)
    parser.add_argument("--budget", type=int, default=180000,
                        help="max characters of context to send")
    parser.add_argument("--write", action="store_true",
                        help="actually write the returned files (default: manifest only)")
    parser.add_argument("--transcript", default=None, help="save the raw reply here")
    args = parser.parse_args()

    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    brief_path = args.brief if os.path.isabs(args.brief) else os.path.join(root, args.brief)
    if not os.path.isfile(brief_path):
        sys.exit("no brief at %s" % brief_path)
    brief = open(brief_path, encoding="utf-8").read()

    chunks, used, skipped = gather(args.context, root, args.budget)
    print("==> %s" % os.path.relpath(brief_path, root))
    print("    model   : %s" % args.model)
    print("    context : %d file(s), %d characters" % (len(chunks), used))
    for item in skipped:
        print("    SKIPPED : %s" % item, file=sys.stderr)

    # Context selection decides the quality of what comes back, and the failure is
    # silent. An early run proposed adding a test that already existed, asserting
    # exactly what that test asserts — a fair inference from having been shown the
    # code and not the tests. Nothing can tell "untested" from "tests not supplied".
    if not any("test" in chunk.split("\n", 1)[0].lower() for chunk in chunks):
        print("    NOTE    : no test files in context. Expect followups proposing "
              "tests that already exist.", file=sys.stderr)

    user = "# Task\n\n%s\n\n# Repository context\n\n%s" % (brief, "\n\n".join(chunks))
    payload = call(read_key(), args.model,
                   [{"role": "system", "content": SYSTEM}, {"role": "user", "content": user}],
                   args.max_tokens)

    raw = payload["choices"][0]["message"]["content"]
    usage = payload.get("usage", {})
    finish = payload["choices"][0].get("finish_reason")
    print("    tokens  : %s in, %s out (%s)" % (
        usage.get("prompt_tokens"), usage.get("completion_tokens"), finish))

    if args.transcript:
        target = args.transcript if os.path.isabs(args.transcript) else os.path.join(root, args.transcript)
        with open(target, "w", encoding="utf-8", newline="\n") as handle:
            handle.write(raw)
        print("    saved   : %s" % target)

    sections, files, unterminated = parse(raw)

    if not files and not any(sections.values()):
        print("the reply matched none of the expected markers.", file=sys.stderr)
        print("--- reply as received (%d chars) ---" % len(raw), file=sys.stderr)
        print(raw[:2000], file=sys.stderr)
        sys.exit(1)

    if finish == "length" or unterminated:
        print()
        print("    WARNING : the reply was cut off. %d file(s) arrived incomplete "
              "and were NOT written: %s" % (len(unterminated), ", ".join(unterminated) or "none"),
              file=sys.stderr)

    print()
    print("--- files ---")
    for path, content in files:
        target = os.path.abspath(os.path.join(root, path))
        if not target.startswith(root + os.sep):
            sys.exit("refusing to write outside the repository: %r" % path)
        verb = "modify" if os.path.exists(target) else "create"
        print("  %-6s %-68s %6d bytes" % (verb, path, len(content)))
        if args.write:
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(content)

    for label in ("NOTES", "CONCERNS", "FOLLOWUPS"):
        if sections[label]:
            print()
            print("--- %s ---" % label.lower())
            for line in sections[label].splitlines():
                print("  %s" % line)

    if not args.write:
        print()
        print("(nothing written — pass --write to apply)")


if __name__ == "__main__":
    main()

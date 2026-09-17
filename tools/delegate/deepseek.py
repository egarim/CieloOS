#!/usr/bin/env python3
"""Hand a task to DeepSeek, get files back, leave them in the working tree.

    python tools/delegate/deepseek.py --brief docs/briefs/invites-01-storage.md \
        --context docs/invites.md src/backend/WorkspaceRuntime.Domain/*.cs \
        --write

The division of labour this exists to serve: DeepSeek does the task, codex
reviews it. That only works if the task arrives with enough context to be done
properly and the result lands somewhere a review can see it — a diff in a branch,
not a paste in a chat window.

Deliberate choices:

* **deepseek-chat, pinned.** The benchmark this project runs compares agents with
  the model held constant; a harness that silently upgraded itself would make
  every past number meaningless. --model exists, but the default does not drift.

* **It writes into the working tree and commits nothing.** The whole point is
  that a human or a reviewer sees a diff before anything is permanent. --write is
  opt-in; without it you get a manifest and nothing touches disk.

* **Refuses to write outside the repo.** A path traversing out of the tree is a
  model mistake at best, so it is a hard error rather than a warning.

* **The key is read from the environment and never printed**, not in errors, not
  in --dry-run, not in the transcript it saves.
"""
import argparse
import glob
import json
import os
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

OUTPUT. Reply with a single JSON object and nothing else:

{
  "files":   [{"path": "relative/path.cs", "content": "<entire file content>"}],
  "notes":   "what you did and why, in prose",
  "concerns": ["anything you think is wrong with the task as specified"],
  "followups": ["work this task implies but deliberately leaves undone"]
}

Every file must be COMPLETE. Never emit a fragment, a diff, or an ellipsis \
standing in for unchanged code: the content you give replaces the file entirely. \
If a file is too large to reproduce in full, say so in "concerns" and leave it \
out of "files" rather than truncating it."""


def read_key():
    key = os.environ.get("DEEPSEEK_API_KEY", "")
    if not key:
        sys.exit("DEEPSEEK_API_KEY is not set. Export it and try again; it is never read from a file.")
    return key


def gather(patterns, root, budget):
    """Collect context files, newest-listed-first, stopping at the character budget.

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
        "response_format": {"type": "json_object"},
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
            # 4xx other than rate limiting will not improve by asking again.
            if exc.code not in (429, 500, 502, 503, 504):
                break
        except Exception as exc:  # network, timeout
            last = str(exc)
        if attempt < retries:
            wait = 5 * attempt
            print("  attempt %d failed (%s); retrying in %ds" % (attempt, last, wait), file=sys.stderr)
            time.sleep(wait)
    sys.exit("DeepSeek call failed: %s" % last)


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
    # silent. The harness's own smoke run proposed adding a test that already
    # existed, asserting exactly the thing it asserts — a reasonable inference from
    # having been shown the code and not the tests. A delegate cannot tell the
    # difference between "untested" and "tests not supplied", so say so here rather
    # than reading the suggestion later and believing it.
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
    if finish == "length":
        print("    WARNING : the reply hit the token ceiling and is probably truncated.",
              file=sys.stderr)

    if args.transcript:
        open(args.transcript, "w", encoding="utf-8").write(raw)

    try:
        result = json.loads(raw)
    except json.JSONDecodeError as exc:
        sys.exit("the reply was not JSON (%s). Re-run with --transcript to inspect it." % exc)

    files = result.get("files") or []
    print()
    print("--- files ---")
    for item in files:
        path = item.get("path", "")
        target = os.path.abspath(os.path.join(root, path))
        # A path escaping the repository is a model error, not something to warn
        # about and continue past.
        if not target.startswith(root + os.sep):
            sys.exit("refusing to write outside the repository: %r" % path)
        exists = "modify" if os.path.exists(target) else "create"
        print("  %-6s %-70s %d bytes" % (exists, path, len(item.get("content", ""))))
        if args.write:
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(item.get("content", ""))

    for label in ("notes", "concerns", "followups"):
        value = result.get(label)
        if not value:
            continue
        print()
        print("--- %s ---" % label)
        if isinstance(value, list):
            for entry in value:
                print("  * %s" % entry)
        else:
            print("  %s" % value)

    if not args.write:
        print()
        print("(nothing written — pass --write to apply)")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Checks the review decisions a pull request adds or changes (docs/review-decisions/README.md).

A finding of the review agents that is rated blocker, critical, high or major and that the author does not fix
(declined or deferred) is the maintainer's to accept: the entry must cite a comment of an approver, on this repository.
Every other entry needs only its reason. The script reads text the author wrote: it only ever parses it, never runs it.
"""
import json
import os
import re
import subprocess
import sys
import urllib.error
import urllib.request

FOLDER = "docs/review-decisions"
GRAVE = {"blocker", "critical", "high", "major"}
LEVELS = GRAVE | {"medium", "minor", "low"}
DECISIONS = {"fixed", "declined", "deferred"}
HEADING = re.compile(r"^## (R-\d+-\d+) \| ([a-z-]+) \| ([a-z]+) \| ([a-z]+)\s*$")
FIELD = re.compile(r"^- \*\*(Finding|Where|Reason|Approved)\*\*: (.*)$")
COMMENT_URL = re.compile(r"^https://github\.com/([\w.-]+)/([\w.-]+)/(?:pull|issues)/\d+#issuecomment-(\d+)$")


def parse(text):
    """The entries of one file: a list of dicts (id, agent, level, decision, fields, line), and the problems met while reading."""
    entries, problems, current = [], [], None
    for number, line in enumerate(text.splitlines(), 1):
        if line.startswith("## "):
            match = HEADING.match(line)
            if not match:
                problems.append(f"line {number}: a heading must be '## R-<pr>-<n> | <agent> | <level> | <decision>'")
                current = None
                continue
            current = {"id": match[1], "agent": match[2], "level": match[3], "decision": match[4], "fields": {}, "line": number}
            entries.append(current)
        elif current is not None and (field := FIELD.match(line)):
            current["fields"][field[1]] = field[2].strip()
    return entries, problems


def check_entry(entry, fetch_comment, approvers, repository):
    """The problems of one entry. `fetch_comment(owner, repo, id)` gives the login of the comment's author, or None."""
    name, fields, problems = entry["id"], entry["fields"], []
    if entry["level"] not in LEVELS:
        problems.append(f"{name}: the level is one of {', '.join(sorted(LEVELS))}")
    if entry["decision"] not in DECISIONS:
        problems.append(f"{name}: the decision is fixed, declined or deferred")
    if problems:
        return problems
    for required in ("Finding", "Where"):
        if not fields.get(required):
            problems.append(f"{name}: the {required} is missing")
    if entry["decision"] == "fixed":
        return problems
    if len(fields.get("Reason", "")) < 12:
        problems.append(f"{name}: a finding that is not fixed needs its Reason")
    if entry["level"] not in GRAVE:
        return problems
    url = fields.get("Approved", "")
    match = COMMENT_URL.match(url)
    if not match:
        problems.append(f"{name}: {entry['level']} and not fixed: it needs 'Approved' with the link of the maintainer's comment")
    elif f"{match[1]}/{match[2]}".lower() != repository.lower():
        problems.append(f"{name}: the approval is a comment of another repository")
    else:
        author = fetch_comment(match[1], match[2], match[3])
        if author is None:
            problems.append(f"{name}: the approving comment cannot be read")
        elif author.lower() not in approvers:
            problems.append(f"{name}: the comment is by '{author}', who is not an approver")
    return problems


def read_approvers(path):
    with open(path, encoding="utf-8") as handle:
        return {line.strip().lower() for line in handle if line.strip() and not line.startswith("#")}


def github_comment(token):
    def fetch(owner, repo, comment_id):
        request = urllib.request.Request(
            f"https://api.github.com/repos/{owner}/{repo}/issues/comments/{comment_id}",
            headers={"Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json"},
        )
        try:
            with urllib.request.urlopen(request, timeout=20) as response:
                return json.load(response)["user"]["login"]
        except urllib.error.URLError:
            return None

    return fetch


def changed_files(base):
    out = subprocess.run(["git", "diff", "--name-only", "--diff-filter=AM", f"origin/{base}...HEAD", "--", FOLDER],
                         check=True, capture_output=True, text=True).stdout
    return [f for f in out.splitlines() if f.endswith(".md") and not f.endswith("README.md")]


def main():
    base, repository, token = os.environ["BASE"], os.environ["GITHUB_REPOSITORY"], os.environ.get("GITHUB_TOKEN", "")
    approvers = read_approvers(".github/review-approvers")
    fetch, failures, count = github_comment(token), [], 0
    for path in changed_files(base):
        with open(path, encoding="utf-8") as handle:
            entries, problems = parse(handle.read())
        failures += [f"{path}: {p}" for p in problems]
        for entry in entries:
            count += 1
            failures += [f"{path}: {p}" for p in check_entry(entry, fetch, approvers, repository)]
    for failure in failures:
        print(f"::error title=Review decision::{failure}")
    print(f"{count} decision(s) checked, {len(failures)} problem(s).")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

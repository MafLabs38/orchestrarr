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
# The ids are written the same way by the author and by the maintainer: no leading zero, so that R-36-1 and R-36-01 are never two entries
HEADING = re.compile(r"^## (R-[1-9]\d*-[1-9]\d*) \| (test|quality|security|architecture|spec|codeql|sonar) \| ([a-z]+) \| ([a-z]+)\s*$")
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


def check_entry(entry, fetch_comment, approvers, repository, pr_number):
    """The problems of one entry. `fetch_comment(owner, repo, id)` gives (login of the comment's author, number of the issue or pull request
    it is on, its text), or None. The approval must be on this pull request: a comment of the maintainer elsewhere approves nothing here."""
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
        found = fetch_comment(match[1], match[2], match[3])
        if found is None:
            problems.append(f"{name}: the approving comment cannot be read")
        else:
            author, on, text = found
            if author.lower() not in approvers:
                problems.append(f"{name}: the comment is by '{author}', who is not an approver")
            if not re.search(rf"\bOK to (?:decline|defer) {re.escape(name)}(?![\w-])", text):
                problems.append(f"{name}: the approving comment must say 'OK to decline {name}' (or 'OK to defer {name}'): any other comment is no approval of it")
            if str(on) != str(pr_number):
                problems.append(f"{name}: the approving comment is on #{on}, not on this pull request (#{pr_number})")
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
                data = json.load(response)
            return data["user"]["login"], int(data["issue_url"].rsplit("/", 1)[1]), data.get("body") or ""
        except (urllib.error.URLError, TimeoutError, ValueError, KeyError, TypeError):
            return None

    return fetch


def changed_files(base):
    out = subprocess.run(["git", "diff", "--name-only", "--no-renames", "--diff-filter=AM", f"origin/{base}...HEAD", "--", FOLDER],
                         check=True, capture_output=True, text=True).stdout
    # Only <number>.md files are entries; any other markdown file in the folder is refused by main(), so that nothing escapes the check
    return [f for f in out.splitlines() if os.path.basename(f) != "README.md"]


def main():
    base, repository, token = os.environ["BASE"], os.environ["GITHUB_REPOSITORY"], os.environ.get("GITHUB_TOKEN", "")
    pr_number = os.environ["PR_NUMBER"]
    if not token:
        print("::error title=Review decision::GITHUB_TOKEN is empty: the approving comments cannot be read")
        return 1
    approvers = read_approvers(".github/review-approvers")
    fetch, failures, count = github_comment(token), [], 0
    for path in changed_files(base):
        if not re.fullmatch(r"\d+\.md", os.path.basename(path)) or os.path.dirname(path) != FOLDER:
            failures.append(f"{path}: the register holds the README and files named <pull request number>.md, nothing else")
            continue
        with open(path, encoding="utf-8") as handle:
            entries, problems = parse(handle.read())
        failures += [f"{path}: {p}" for p in problems]
        for entry in entries:
            count += 1
            failures += [f"{path}: {p}" for p in check_entry(entry, fetch, approvers, repository, pr_number)]
    for failure in failures:
        print(f"::error title=Review decision::{failure}")
    print(f"{count} decision(s) checked, {len(failures)} problem(s).")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())

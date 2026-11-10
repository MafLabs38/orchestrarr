# Review decisions

> What was decided about the findings of the review agents, so that a finding already answered is not raised again, and so that the maintainer sees every serious finding that is not fixed ([ADR 0022](../adr/0022-review-findings-are-answered-in-a-register-the-agents-read.md)).

The review agents are made to find defects, so they always find some. This register is how their advice is **taken without being followed blindly**: each finding gets an answer, written once, and the agents read the answers.

## Three answers

| Answer | When | Who decides |
|---|---|---|
| **fixed** | The finding is right | The author |
| **declined** (or **deferred**), rated medium, minor or low | A preference, a hardening beyond the threat model, a defect that does not occur: the reason is written | The author |
| **declined** (or **deferred**), rated **blocker, critical, high or major** | The author does not want to fix a serious finding | **The maintainer**, in a comment on the pull request, before the entry is merged |

A medium finding is still answered (fixed, or declined with its reason); only the four serious levels need the maintainer. The levels of the agents are mapped: blocker, critical, high and major are the serious ones; medium; minor and low (the tests reviewer's *remove / fix / add*: a *fix* that is right is fixed, the others are answered like any finding).

## One file per pull request

`docs/review-decisions/<pr number>.md`, one entry per finding, in this exact shape (the CI reads it):

```markdown
## R-44-3 | security | high | declined

- **Finding**: what the agent said, in one line.
- **Where**: the file, or the component.
- **Reason**: why it is declined or deferred; for a fix, optional.
- **Approved**: https://github.com/<owner>/<repo>/pull/44#issuecomment-123456789
```

The heading is `R-<pr>-<n> | <agent> | <level> | <decision>`: the source is `test`, `quality`, `security`, `architecture`, `spec`, `codeql` or `sonar`, the level the one the agent gave (blocker, critical, high, major, medium, minor, low), the decision `fixed`, `declined` or `deferred`. `Approved` is required, and only then, for a serious finding that is not fixed.

## What enforces it

- **The agents read this folder** (their instructions say so) and do not raise again a finding that an entry already answers, unless the code changed so that the reason no longer holds. A serious entry without a valid approval is not trusted: the agent raises the finding.
- **The `review-decisions` job** fails a pull request that adds or changes an entry which is serious, not fixed, and without the link of a comment by an approver (`.github/review-approvers`) in this repository. The job runs the checker and the list of approvers **of the base branch**: a pull request cannot approve itself. After the maintainer has commented, re-run the job.
- The README of the folder, the checker, the approvers, the job and the agents' instructions are in the maintainer's tier ([ADR 0015](../adr/0015-where-the-launcher-gets-what-its-checks-read.md) §3, CLAUDE.md rule 15): the author of a pull request does not edit the rules that judge it without the maintainer. The entries (`<pr number>.md`) are the author's to write. The approving comment must be **on this pull request and name the finding** ("OK to decline R-44-3", or "OK to defer R-44-3"; any other comment is no approval); the level in the heading is the author's word, which the maintainer reads in the table of the pull request.

## What the author does

1. Read every report of the pipeline's agents on the head commit; check each finding in the code.
2. Write an entry per finding in the file of the pull request. Fix what is right.
3. For a serious finding that you would not fix: **stop and tell the maintainer** (which finding, what it would cost, what you recommend). They answer in a comment on the pull request; its link goes in `Approved`.
4. Summarise in the *Review* section of the pull request, pointing to the file.

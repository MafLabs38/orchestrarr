# 0027 — The table shows what can be decided, by application, and keeps what was decided

- **Status**: proposed (asked for by the maintainer on 2026-11-12; accepted when the pull request that carries it is merged)
- **Date**: 2026-11-12
- **Spec**: D-75, D-76, D-77 (refines D-67 and ADR 0019; supersedes ADR 0003 for what the table shows)

## Context

The first real run of the table on the real fleet showed 135 rows, and a person who works with it every day would have given it up: rows that come back every time and cannot be acted on (images of the platform, tags that move, repositories nobody has touched since June), rows that are wrong (a CI "failing" because SonarQube and the review agents cannot run for a bot's pull request; a Node base moved to an odd version that is never a long-term release; an image to bump to a new major version where it is a migration), rows with a name that is not an application (a repository, the name of an image), and no way to say "not this one" or to see what was decided before. The audit that compared every row to the infrastructure found that about a third were fine as shown, a third right but badly told, and a third noise.

## Decision

1. **A row is shown where a person can do something with it.** Each row belongs to a **tab**: *To decide* (something can be done now: this is the default), *To configure* (it could be done and a setting is missing: a power not granted, no Runner, no description of the application) and *To know* (information: nothing is defined to do about it). The server gives the tab (`tab`), and a row an approval is working on stays in *To decide* (`inProgress`).
2. **The row is the application.** When the source of the fleet says that a repository, or an image, is one application, the row carries its name and its icon (`application`, `applicationId`, `icon`); the repository or the image stays in `source`. A repository of several applications, or of none known, keeps its own name. The icons are the source's: the host relays `GET /api/v1/icons/{name}` (Portarr serves them), with a policy that lets a picture be shown and nothing else, and the page falls back to two letters.
3. **Rules that keep a row honest**, each one a test:
   - a check that cannot run for a bot's pull request (SonarQube, the review agents; `GitHub:ChecksThatNeedSecrets`) is "CI not run", never "failing", and the row stops at the validation of the Runner instead of becoming a repair;
   - a pinned image that moved to another digest is the class *digest*, a group of packages is the class *group*;
   - Node is moved only to a long-term release (an odd version is never one);
   - a repository with no push for 120 days and a red build is one row, *Dormant repository* (archive it or fix it), in place of its pull requests and its build;
   - an image of the platform (K3s) is updated with it, a new major version of an image is a change to plan, not a tag to bump, and an image under a moving tag is a row of its own (pin a version);
   - "Dependabot is off" says what it means.
4. **A row can be skipped.** The person sets it aside; it leaves the table, the history keeps it, and it comes back **only if its target changes** (the description of the row: «express 4.16.0 → 5.3.0» is skipped for that bump, not for the next). A skip can be taken back, is traced in the trail before it is made, needs the right to approve (not a second factor: it runs nothing) and is refused when the row is not what the person saw.
5. **What was decided is kept.** Every approved row and every skipped row is photographed when it is decided (application, text, target, pull request, commit, who, when, the reason of a skip) in the history, a table of the database (schema version 7): the live row is gone once the pull request is merged. What became of an approved line is read from the approval and the run while it is going on, and kept in the line as soon as it is known (below). The history is not the default view; it is read a page at a time on the server (newest first, 100 by default). Each line also carries its **outcome**, read from the approval and the Runner's results and kept when it is known (*in progress*, *passed*, *failed*, *repaired*, *refused*, *expired*, *ended*; a skip is *skipped*). Setting a row aside and taking it back are written to the audit trail first (`problem.skipped`, `problem.unskipped`): a skip that cannot be traced is not made. The rules of relevance take their values from the `Relevance` section (the delay of a dormant repository, the namespaces of the platform, the packages moved only to a long-term release), with the defaults above.

## Consequences

- The table is a few dozen rows to decide, not a hundred and thirty-five to read. What is left out of *To decide* is not lost: it is in the two other tabs, and the history.
- The history grows without bound; its retention (12 months, then archived) is built with the archive of the trail (SPEC Q-24).
- The model of the fleet gains the applications (names, icons) and the day of the last push; the Portarr provider reads `GET /api/v1/apps` besides the maintenance report, and an unreadable list of applications costs only the names.
- The names of the applications come from Portarr: a repository or an image that Portarr does not link to an application keeps its own name. Linking them (the repository of each application in the catalogue) is Portarr's, and the table improves as it is done.
- What the audit found and this decision does not do: grouping the same change across repositories into one decision, the fix of the version in the infrastructure files (the next pull request), and a Runner that merges.

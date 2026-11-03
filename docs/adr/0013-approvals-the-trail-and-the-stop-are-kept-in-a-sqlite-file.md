# 0013 — The approvals, the audit trail and the global stop are kept in a SQLite file

- **Status**: proposed
- **Date**: 2026-11-03

## Context

[ADR 0010](0010-approving-records-a-decision-and-the-sandbox-operator.md) built the approval with stores in memory, and listed what must exist before a launcher does: a global stop that is **persistent and starts engaged** when its state cannot be read, and approvals and a trail that are **persistent**, the store enforcing the uniqueness of a decision itself. A restart forgot the queue, the trail and the stop, which a sandbox accepts and production does not: a stop that a restart releases is a stop that does not stop.

## Decision (proposed)

1. **One SQLite file** holds the three (`Storage:Provider=Sqlite`, `Storage:Path`). SQLite is what Mnemarr already uses, through `Microsoft.Data.Sqlite` without an ORM, and it keeps Orchestrarr a single container with one volume. `Memory` stays the default of the sandbox; **the image sets `Sqlite` and the file `/config/orchestrarr.db`**, in a volume `/config` (the convention of the *arr images) that only the process can open. The storage lives in its own project, `Orchestrarr.Storage`, behind the interfaces of Core; the routes and the services did not change.
2. **The database itself holds the rules**, not only the code: a unique key on repository, pull request, commit and steps, so that two processes cannot both record the same decision; and triggers that refuse any update or delete of the audit trail, which is append-only. Writing is all or nothing, in a transaction that takes the write lock first.
3. **An approval and its trace are one write.** A narrow port, `IApprovalLedger`, records the approvals and the entry that traces them in the same transaction: either both are in the file, or neither is. A decision already there answers "already approved" without a second entry.
4. **Unknown means engaged.** A global stop whose state cannot be read counts as engaged. Engaging always takes effect, even when it cannot be saved: it is then held in memory, and the status says `killSwitchSaved: false` (the interface warns that a restart would release it). Releasing is the delicate direction: the release must be **saved** (else 503 `killSwitchUnavailable`) and **traced** (else 503 `auditUnavailable`, and the stop is put back engaged), because a release nobody can see is worse than a stop that stays on.
5. **A database that did not exist starts engaged**, outside Development: a new file is also a lost file, and "nothing was ever stopped" cannot be told from "the state was erased". The host traces `instance.created` and the operator releases the stop once, deliberately. In Development a new file starts released.
6. **An approval or an entry that cannot be written is refused, never lost.** The trail answers "not recorded" and the product fails closed (`auditUnavailable`). A file that cannot be used stops the host **at its start**, not at the first approval; a persistent store with nowhere to persist is never a quiet fallback to memory. A file that becomes unreadable later makes the reading routes fail (never an empty list) while the status still answers.
7. **The capacities depend on where things are kept**: 500 approvals and 2,000 entries in memory, 10,000 and 200,000 in a file (`Approvals:QueueCapacity`, `Approvals:AuditCapacity` override both). One policy, shared by both implementations, refuses approvals and refusals before the trail is full, so that the last quarter is kept for the global stop and the decisions that matter.
8. **The status says where things are kept** (`storage.kind`, `sqlite` or `memory`), and the interface shows it, with a warning when it is memory. A host with the sign-in on and the store in memory, outside Development, warns in its log that everything is lost at a restart.
9. **The names of the steps are the file format.** A step is stored by the name of its `StepKind` (`Validate`, `MergePullRequests`, …): renaming one is a schema change, with a new `user_version`.

## Consequences

- An engaged stop stays engaged after a restart, and the trail and the queue survive it. Nothing yet takes an approval, so the lifecycle of an approval (taken, done, expired, superseded when the pull request moved on) comes with the launcher that does.
- The file is a volume to keep and to back up, with `sqlite3 .backup` or `VACUUM INTO` (a copy of the file alone, while the host runs, can be torn). It holds identifiers of people (the subject of the identity provider) and their decisions, not credentials: erasing a person on request is not possible without breaking the append-only rule, which is a question to settle with the retention below.
- **One replica, on a local disk.** SQLite's locks are not reliable on a network share, and a rollout that starts the new pod before stopping the old one makes two writers. The deployment uses a single replica with a `Recreate` strategy, as the sign-in already does (ADR 0011).
- The schema is versioned with `PRAGMA user_version`; a database of a newer version is refused, not damaged.
- A tamper-proof trail would need more than a trigger: someone who can edit the file can drop the triggers. The append-only rule defends against the application and against mistakes, not against the owner of the volume. Signing or shipping the trail elsewhere is a later question.

## Not decided here

- The lifecycle of an approval, and who moves it.
- Retention of the trail past its capacity (today it refuses when full; archiving it, and erasing a person, are later decisions).
- Keeping the trail outside the host (a signed export, or a log shipper).
- A stop that is engaged and held in memory while the trail also cannot be written: it works, but leaves no trace until the file comes back.

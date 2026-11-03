# 0013 — The approvals, the audit trail and the global stop are kept in a SQLite file

- **Status**: proposed
- **Date**: 2026-11-03

## Context

[ADR 0010](0010-approving-records-a-decision-and-the-sandbox-operator.md) built the approval with stores in memory, and listed what must exist before a launcher does: a global stop that is **persistent and starts engaged** when its state cannot be read, and approvals and a trail that are **persistent**, the store enforcing the uniqueness of a decision itself. A restart forgot the queue, the trail and the stop, which a sandbox accepts and production does not: a stop that a restart releases is a stop that does not stop.

## Decision (proposed)

1. **One SQLite file** holds the three (`Storage:Provider=Sqlite`, `Storage:Path`). SQLite is what Mnemarr already uses, through `Microsoft.Data.Sqlite` without an ORM, and it keeps Orchestrarr a single container with one volume. `Memory` stays the default of the sandbox; **the image sets `Sqlite` and a volume `/data`**. The storage lives in its own project, `Orchestrarr.Storage`, behind the interfaces of Core; the routes and the services did not change.
2. **The database itself holds the rules**, not only the code: a unique key on repository, pull request, commit and steps, so that two processes cannot both record the same decision; and triggers that refuse any update or delete of the audit trail, which is append-only. Adding approvals or entries is all or nothing, in a transaction that takes the write lock first.
3. **Unknown means engaged.** A global stop whose state cannot be read counts as engaged. Engaging always takes effect, even when it cannot be saved (it is held in memory until a release is saved), because the stop must work when nothing else does. Releasing a stop whose release cannot be saved raises `KillSwitchUnavailableException`, the route answers 503 `killSwitchUnavailable`, nothing says it was released, and it stays engaged. A fresh database starts released: nothing was ever stopped.
4. **An approval or an entry that cannot be written is refused, never lost.** The trail answers "not recorded" and the product fails closed (`auditUnavailable`). A file that cannot be used stops the host **at its start**, not at the first approval; a persistent store with nowhere to persist is never a quiet fallback to memory.
5. **The status says where things are kept** (`storage.kind`, `sqlite` or `memory`), and the interface shows it, with a warning when it is memory. A host with the sign-in on and the store in memory, outside Development, warns in its log that everything is lost at a restart.

## Consequences

- An engaged stop stays engaged after a restart, and the trail and the queue survive it. Nothing yet takes an approval, so the lifecycle of an approval (taken, done, expired, superseded when the pull request moved on) comes with the launcher that does.
- The file is a volume to keep and to back up. It holds identifiers of people (the subject of the identity provider) and decisions, not credentials. A shared file between replicas is possible only through SQLite's own locking: the host is a single replica, as the sign-in already is (ADR 0011).
- The schema is versioned with `PRAGMA user_version`; a database of a newer version is refused, not damaged.
- A tamper-proof trail would need more than a trigger: someone who can edit the file can drop the triggers. The append-only rule defends against the application and against mistakes, not against the owner of the volume. Signing or shipping the trail elsewhere is a later question.

## Not decided here

- The lifecycle of an approval, and who moves it.
- Retention of the trail past its capacity (today it refuses when full; archiving it is a later decision).
- Keeping the trail outside the host (a signed export, or a log shipper).

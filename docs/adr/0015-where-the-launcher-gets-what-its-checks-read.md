# 0015 — Where the launcher gets what its checks read: delivery, time and a first slice that is a dry run

- **Status**: proposed (safe defaults; the maintainer has not decided them, and Q-25, Q-26 and Q-27 are open)
- **Date**: 2026-11-04

## Context

[ADR 0014](0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md) decides that the launcher is a Rust binary held to the control plane's rules by shared conformance vectors. It leaves open what the launcher reads when it decides: the order, the grants, what production is, the stop, the trail, the time. [SPEC](../SPEC.md) D-46 says the launcher "pulls signed orders from the store"; with the control plane's file a single-replica local store ([ADR 0013](0013-approvals-the-trail-and-the-stop-are-kept-in-a-sqlite-file.md)), the store has to be behind the control plane's API. Writing this down also exposes what must hold for the launcher to stay an authorization independent of the one that signs, so this ADR records both the proposal and its constraints.

## Decision (proposed)

The launcher must stay an authorization **independent** of the one that had the order signed (ADR 0007 rule 2: whoever gives an order is authorized twice). If it took its grants and "is this production" from the same API that had Vault sign the order, a compromised control plane could sign any order *and* tell the launcher it is allowed. The reviews of [ADR 0014](0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md) found what has to hold. **None of it is decided**; this is the constraint row 3a starts from.

### 1. Orders and delivery

- The launcher pulls through the control plane's API, never from the file of [ADR 0013](0013-approvals-the-trail-and-the-stop-are-kept-in-a-sqlite-file.md), and opens no port (SPEC D-46).
- The API **delivers** an order to the launcher identity of the order's environment and to no other, **once**. The delivery is recorded with the approval lifecycle (ADR 0013). ("Delivery" is not a Vault lease, the word D-43 uses.)
- A decision is accepted only from the identity that received the order, and is recorded and acknowledged by the API **before the run starts**.
- A delivered order not decided by its expiry goes back to waiting. **An approval is issued again only if no decision was recorded for it**; whether it can be issued again without the person is settled with row 3a (a production step-up cannot).
- The order is **proposed to be signed when it is delivered** (D-52 says at run start today), after the control plane has checked again, at that moment, that the approver is still an operator and that the plan is unchanged (`planChanged`, ADR 0010); if it cannot check, it does not sign. A launcher in its quiet period does not pull, so an approval does not go stale, and the card says that a launcher is in it.
- The launcher does not pull while it cannot accept: in its quiet period, with a clock not known to be synchronized, without its claim, or while the stop is engaged.
- These are guards of the API, in addition to the launcher's own, not independent of it.

### 2. Grants and production (amends D-10, D-11, D-17)

- The launcher's granted powers, its production flags (one per served environment, and one for "no environment"), its audience and the environments it serves are its **own configuration**, never asked of the API.
- This **reverses on purpose** what Core does today (`PolicyGrantedPowers`: one source of grants for the card and the launcher). An independent authorization is worth more than one source.
- A power therefore acts in **two places**, and the effective power is the intersection. A card can offer a step that the launcher refuses with `OutsideGrantedPowers`, which contradicts D-11 as written, so it **amends D-11** (and D-10).
- To keep the card honest, the launcher publishes the grants it enforces (or a digest) through the API, and the control plane intersects them **for display only**; the launcher enforces its own copy.
- For production, the control plane keeps classifying from the environment description (D-17) and the launcher from its own flags: an exception to D-17 for the launcher only. When they disagree the launcher's answer wins and the step-up is refused late; the control plane shows the launcher's published flags beside Portarr's classification, so that the drift is visible.
- The matching of grants is held by the vectors (ADR 0014, point 2).

### 3. What can change the launcher

A check in the control plane is worth nothing against a compromised control plane, so the guards are elsewhere. The files concerned are the **launch contract files**, listed here once (CLAUDE.md rule 12 and SPEC D-61 point to this list): `launcher/`, `contract/`, the C# oracle in `src/Orchestrarr.Core/Launch/` and the grant matching it relies on (`src/Orchestrarr.Core/WorkItems/ConfiguredPowerPolicy.cs`, `PolicyGrantedPowers.cs`), their tests (the vector generator included), the Dockerfiles and `.dockerignore`, `CLAUDE.md` and `.github/CODEOWNERS`, the secret-scan settings (`.gitleaksignore`, `.gitleaks.toml`), and the workflow files that run the conformance lane and the image scan; plus, outside this repository, the launcher's configuration, the issuer's map from environment to audience, the launcher's image and its deployment.

- **The coding agent** does not merge a pull request that touches them (SPEC D-61, proposed; followed as a precaution, enforced by nothing but its instruction today).
- **At the forge**: Orchestrarr's identity is not installed on the repository of the launcher's configuration, and a ruleset (code owners) on those paths requires the review of a person that neither the coding agent nor Orchestrarr's identity can give. Proposed; `CODEOWNERS` has a single catch-all rule today.
- **In the launcher**: its configuration lists protected repositories, and its grant matching refuses any order on one of them whatever the grants say (a vector).
- **At the deployment**: the launcher runs a pinned image digest that a person changes, not the newest tag that D-50 deploys for the control plane (an exception to D-50); its manifests, in the cluster's GitOps repository, change only by a person's review; and the cluster credential issued to a run cannot write the launcher's namespace, nor the claim that lives there.

### 4. Ledger and time

The nonce ledger is **in the launcher's memory**: replay protection does not depend on the API, and nothing is kept on disk. It is sound under these conditions, all from the launcher's **own** configuration, never from an order or the API:

- **A quiet period at every start**: nothing is accepted for its own `MaxLifetime + ClockSkew`, on its monotonic time. An order spent before a restart has expired by then, which also covers a recreated volume, a restored snapshot and a copy that starts later.
- **An issue-time floor**: the launcher keeps its wall-clock start time and refuses any order issued before it minus the tolerance, so that a wall clock stepped backwards cannot revive an expired order.
- **A clock known to be right**: nothing is accepted until its host reports the clock synchronized. A clock wrong at the start is a deployment requirement, not something the launcher can prove.
- **Exactly one process holds a launcher's identity at a time.** The launcher takes an **exclusive claim** on it before it pulls (a Kubernetes Lease in its own namespace where it runs in a cluster, an equivalent lock elsewhere: a claim that does not go through the control plane's API) and pulls only while it holds it.
- The claim has a **time to live** (configuration, 1 minute by default). The launcher measures it on its monotonic clock from its last successful renewal, and **refuses to accept**, not only to pull, when that renewal is older than the time to live minus a margin: an old holder that was paused cannot accept after a new one took over.
- **A new holder starts with the quiet period**, at least `MaxLifetime + ClockSkew` plus the time to live. A handover, a rolling update or a replica started by mistake cannot make a spent order run twice. A copy on another host with a claim store of its own is outside the guarantee; the delivery-once rule of the API and the binding of the Vault identity narrow it.
- The dry run spends ids as a real run would. The vectors script the time since the start and the wall clock, and have the boundary cases.

### 5. Stop and trail

- The channel to the API is **authenticated in both directions** (mutual TLS or equivalent); plain HTTP is refused.
- The global stop is read as a **short-lived signed statement**, verified like an order, so that a false API cannot say it is released. The statement carries a **sequence number**, and the launcher refuses one lower than the highest it has seen since its start (a restart forgets it; the quiet period covers that gap). **Unknown, expired or unverifiable means engaged.** A local stop on the launcher's machine is added (an addition to D-43 and F-SAF-01).
- Every launch therefore depends on the control plane being reachable: an outage of the central container stops every environment, which D-43 accepts for a product that fails closed.
- The trace is written through the API, and if the launcher cannot record it accepts nothing.
- The API is not the only record, since a compromised one could acknowledge a write and drop it. The launcher also writes the same entries, **hash-chained** (each carries the hash of the previous one), to its standard output, which the platform's log shipping collects where the control plane cannot write; the Vault audit device and the command logs of the machines record independently (ADR 0006 point 6). An audit compares them with the control plane's trail.
- The audit route accepts a machine identity (stable per launcher, distinct per environment: a requirement on Q-25), never the approval, release or stop routes, and two kinds of entry only: a decision tied to an order that exists and was delivered to that identity, and an **untrusted refusal**, that is any refusal before the signature is verified (the stop, the quiet period, the clock, the verifier, the signature, the shape of the order), with no order id and the subject `unverified`, which never changes an approval.
- The route takes only the event names of the contract and a bounded detail checked against the audit entry of the vectors, limits the rate per order and per identity, and **deduplicates** untrusted refusals per identity and refusal code, one entry per window with a counter, so that a compromised identity cannot fill the trail that approvals depend on.
- How the launcher authenticates to the API is open (SPEC Q-25).

### 6. The first slice is a dry run

- The Rust launcher's first slice pulls, verifies, writes to the audit trail what it would have started, and starts nothing; it holds no credential to Vault, no access to a forge and no container runtime. It proves the chain (the order, the signature, the checks, the pull, the trace) before any power exists.
- **A dry run takes the approval exactly as a real run would**: the trail names it with an event of its own (`launch.dry-run`), the approval ends in a terminal state flagged as a dry run that a real launcher never takes, and the operator approves again for a real run.
- It needs Q-25 and Q-26 settled first: the routes a machine uses are the first not used by a person and must not share the authorization of the routes for people.

## Consequences

- Annotated with this proposal: D-10, D-11, D-17, D-19, D-43, D-46, D-50, D-51, D-52, D-53, and rule 12 of CLAUDE.md (D-61).
- Row 3a changes Core before any vector is generated: the quiet period, the clock check, the issue-time floor and the served environments (ADR 0014 point 6) are added to `Launcher` and `LauncherOptions` (no default audience, a `production` flag per served environment), D-11 is amended, the issuer addresses an order to the launcher of its environment (a map from environment to audience, which retires the shared `"launcher"` default), and the lifecycle of an approval (ADR 0013) gives "an order that exists and was delivered" a meaning.
- The dry run cannot ship before the approval-lifecycle migration of ADR 0013 (today a decision is approved once, ever); that migration comes first in row 3a.
- Tested with row 3a: **the API side of the delivery** (one delivery, the identity of the order's environment only, a return to waiting at expiry, a decision only from the identity that received it, with `FakeTimeProvider`); **the audit route** (machine identity only and refused on the approval, release and stop routes, the two kinds of entry, the contract's event names, the size, the rate, the deduplication, an untrusted refusal never changing an approval); **the launcher's start-up** (it refuses to start with the published test key, without an audience or with `launcher`, and an unknown stop counts as engaged; it pulls nothing without its claim, in its quiet period, with an unsynchronized clock or with the stop engaged); and **the dry run** (no adapter to Vault, a forge or a container runtime in its composition; its entry written once per order; the approval ending in the dry-run terminal state that a real launcher never takes, and a second approval needed).
- A restart or a handover of the launcher costs up to `MaxLifetime + ClockSkew + the claim's time to live` of acceptance (11 min 30 s at the ceiling of D-51 with the default time to live), and every environment stops when the central container is down: both are the price of failing closed (D-43), and are accepted here only as a proposal.
- A power acts in two places, so a card can offer a step that the launcher refuses until the published grants are shown beside it.

## Not decided here

- When the order is signed relative to the approval, how it is issued again after it expires undelivered, and what a production step-up that has gone stale means for that (settled with row 3a, with the open question of [the security model](../security-model.md) on the refresh of the operator's session).
- Whether the exclusive claim and its loss, and the stop statement, are part of the contract of ADR 0014 (a refusal code, a place in the order of the checks, a vector). The stop statement needs its own canonical form and tag, a key usage distinct from the orders', an audience, a sequence and a maximum age (which bounds the time a stop takes to reach a launcher; 1 minute is the proposed default), so that it cannot be parsed as an order or replayed: settled with row 3a before the vectors are generated.
- The machine routes (delivery, decision, stop statement, published grants, audit; SPEC Q-27): the allowed identity, the rate and the size of each, written before the first is built; the publication of grants is a machine write that changes what a card shows and gets the audit route's care.
- Q-25: how the launcher authenticates to the API, with an identity stable per launcher and distinct per environment. Q-26: who runs an order with no environment.
- Whether the launcher runs in the production cluster or on the workshop host (ADR 0012 only says one per environment), and the claim mechanism outside a cluster: one open point (SPEC Q-27) that row 3a closes before the dry run spends ids.

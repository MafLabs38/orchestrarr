# 0014 — The launcher is a Rust binary, held to the control plane by shared conformance vectors

- **Status**:
  - **Accepted** by the maintainer on 2026-11-04: points 1 to 4 (the language and the vectors), except the clause of point 1 on state on disk and the clauses of points 2 and 3 marked proposed.
  - **Proposed** safe defaults: the marked clauses, points 5 and 6, and point 7 ([ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md)).
- **Date**: 2026-11-04

## Context

[ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) and [ADR 0007](0007-a-dedicated-vault-and-a-small-control-plane.md) make the launcher a boundary of its own: first a module with an interface, then a separate process, then a separate host. They say it is "small and boring on purpose" and that it has no inbound port, but **they do not say in which language it is written**. Today it is C# library code in `Orchestrarr.Core` (`Launcher`, the order's canonical form, the refusal codes), tested and not run by any process.

Two facts weigh on the choice:

- **The launcher is the most sensitive component.** It holds the workload identity that Vault answers to (one per environment, [ADR 0012](0012-one-launcher-and-one-vault-role-per-environment.md)) and it is the only thing able to start an agent. What it carries is worth keeping small: no runtime, no shell, few dependencies.
- **The control plane is allowed to be heavy.** It is one central .NET container; the maintainer does not mind its weight. The launcher, one per environment, is the one that should be light. Portarr already made this trade for its machine-side probe (its ADR 0011: the reader, a Rust binary under 1 MB, in an image that holds nothing else), and Mnemarr's collector is also Rust.

Writing the launcher in a second language means **two implementations of the same security rule**: the canonical bytes that are signed, the checks and their order, the refusal codes. If they ever differ (a field written another way, a check moved, a clock tolerance applied to the other side), an order could pass in one and not in the other, or worse, mean something else. That is the real cost, and it is a security flaw if left to chance.

## Decision

1. **The launcher is a Rust binary**, in its own Cargo project (`launcher/`), built into its own image (a static binary built against musl, no shell, no dynamic library, no runtime). It opens no port. *(Proposed, with [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md): it keeps no state on disk, which relies on the ledger conditions of that ADR.)* It is not a dependency of the host and the host is not a dependency of it. The footprint is a goal, not a number to defend: the reader's order of magnitude (about 1 MB, a few MB of memory) is the reference.
2. **The contract between the two implementations is the signed order, written down once, in a language-neutral form**, and held by **shared conformance vectors**: data files, in `contract/launch/`, that both implementations must satisfy. The contract is:
   - the canonical form (`orchestrarr-order-v2`) and the signature scheme (the scheme itself is proposed, point 5);
   - the checks the launcher makes, **in order**, with the refusal code of each (`LaunchRefusal`); the complete ordered list, the one that counts, is written once in `contract/launch/README.md` (which does not exist yet: until then, Core's checks in `Launcher.cs` are the accepted base, and the placements below are relative to them). Proposed additions (points 5 and 6, [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md)), each with its place:
     - `QuietPeriod`, right after the stop;
     - `ClockNotSynchronized`, right after `QuietPeriod`;
     - `EnvironmentNotServed`, right after `WrongAudience` (point 6);
     - `IssuedBeforeStart`, right after `NotYetValid`;
   - the defaults of the options: lifetime ceiling, clock tolerance, age of the second factor;
   - **the grants and how they match** (that the launcher holds its own copy is proposed, [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md)): the format of a grant and the rule that decides whether an order's steps, repository and environment are inside the grants (step × repository × environment, the wildcard, nothing granted means denied);
   - **what production is** (the launcher's own list is proposed, ADR 0015): the launcher's own list of production environments, an environment it does not know being production;
   - **the audience and the served environments**: configuration of each launcher, with the rules of point 6;
   - the audit entries (event name, subject, detail) that a decision writes, and what an unverified order is never allowed to write;
   - what a decision spends: an order refused before the last check does not burn its id; an order that passed every check but the final read of the stop stays spent.
3. **Two kinds of vectors, produced by the control plane's side, in C#, and committed.**
   - **Decision vectors.** One case: the fields of the order, the launcher's configuration (audience, served environments, grants, production environments, options), **a script of the answers of the ports in the order they are called** (the time since the start, the stop read first, the verifier's answer or its failure, the ledger's answer, the stop read a second time, the audit sink's answer or refusal, and the time), because the order of the checks and what each one spends cannot be expressed by a single snapshot of the world; and the expected outcome: the refusal code, or accepted with the canonical bytes of the **normalized** order that is to be run, the audit entries written, and whether the id was spent. There is one vector per refusal (a test fails when a refusal code has none), one for the accepted order, a set for the grants, and the awkward ones: absence against the empty string, steps unsorted or repeated, times with fractions, a field that moves a boundary, an order of another form, an environment not served, an order with none.
   - **Signature vectors** (proposed, with point 5). The verifier is a port in the decision vectors, so they cannot test a signature. These run against the **real** verifier of each side: a public key, bytes and a signature in; valid or not out. They hold the cases that two libraries may judge differently: a malleated signature (`S + L`), a small-order `R`, a small-order public key, a non-canonical encoding, a truncated or over-long signature, a malformed `vault:vN:` prefix, a signature of another version.
4. **Both suites run all the vectors.**
   - The **intended outcome of each case (its refusal code, whether its id is spent) is written by hand in the C# test that defines the case**. The generator produces only what follows from it (the bytes, the signature, the trail) and **fails, before it writes any file, if the C# launcher disagrees with the declared outcome**; otherwise a check moved in C# would regenerate the files silently and the comparison would prove nothing.
   - The files are written deterministically (sorted keys, LF line endings, invariant culture: the same bytes on Linux and on Windows); the .NET tests regenerate them and fail when the committed ones differ.
   - The Rust tests read the same files and check that the Rust launcher gives the same outcome, byte for byte on the canonical form. Each suite fails on a file it did not read.
   - **A change to the form, to the signature scheme, to a check or its order, to a refusal code, to the grants' matching or to a default is one pull request that changes the C# code, the Rust code and the vectors together**; a change of the wire form also changes the tag.
   - The C# `Launcher` stays in Core as the executable reference and as the oracle of the vectors; it runs in no process.
5. **(Proposed.) The signature scheme is asymmetric; the vectors sign with Ed25519**, with a key pair made for the tests and published in the vectors, in the form Vault's transit engine gives (`vault:vN:` and the signature), under a key version that production never uses (`vault:v0:`). Ed25519 is deterministic (the files do not change on every run) and Vault's transit engine offers it; another asymmetric scheme would be a change of the scheme, hence one pull request (point 4). **Verification is strict in both implementations**: a non-canonical `S` (`S ≥ L`) is rejected, a small-order or non-canonical public key or `R` is rejected, and the cofactorless equation is used; the signature vectors hold it. The Rust crate and its strict mode, and the C# Ed25519 library (the generator signs with it, the oracle verifies with it), are chosen with row 3a. The production verifier (a public key, the key version bound) comes with the Vault adapter ([SPEC](../SPEC.md) Q-23).
   - **The test key is test data.** The **private** key lives only in the test data of `contract/launch/`, read by the generator at run time and never embedded in an assembly (so that `COPY src/` cannot carry it), and no build or image includes that directory; a release embeds the published **public** key only as a deny-list. The launcher **refuses to start** when its configured public key is the published one, comparing the **decoded 32 raw bytes**, and a release refuses any order whose key version is `vault:v0:`, by a **deny-list of key versions in the launcher's configuration** (`v0` denied by default and allowed only in the test configuration), never by a compile-time switch, so that the verification path the vectors prove is the one that ships. **That deny-list is defence in depth only**: the version prefix is chosen by whoever writes the signature, so the real barrier is the start-up refusal, which applies to **every verification key the launcher is configured with** (every version of the order key, and the key of the stop statement of ADR 0015), the version selects **exactly one** key (no "try every key" fallback), and the vectors have a case for the test key under a non-`v0` version and one for the test key as the stop key.
   - `contract/` is in the control plane image's `.dockerignore` now; the launcher image's build context must exclude it too (a `.dockerignore` of its own or an explicit `COPY` list), and a check fails if the private key itself is found in a built image: the scan searches the images for the key's bytes, in the encodings it is published in (not for a hash of it).
   - The gitleaks allowance for the private key is added with the vectors and allows **the one finding** (its gitleaks fingerprint in `.gitleaksignore`), not the directory; the C# generator refuses to write a vector signed with any other key.
6. **(Proposed.) The audience and the served environments are configuration of each launcher** (one per environment, ADR 0012).
   - The audience has **no default**: the Rust launcher refuses to start without one, with the literal `launcher` or with a deny-listed test value. Core's `LauncherOptions` loses its default audience (`"launcher"`) in row 3a; the oracle then takes the audience from the configuration of each vector.
   - That two launchers do not share an audience cannot be checked by a launcher, which knows only its own configuration: it is checked where every audience is visible, in the issuer's map from environment to audience (the host refuses to start with a duplicate) and in the infrastructure repository that delivers the launchers' configuration.
   - An order's environment **must be one the launcher serves**, otherwise it is refused as `EnvironmentNotServed`, by a check that runs right after the audience check; the environment wildcard of a grant ranges over the served environments only.
   - An order with **no environment** (a merge only, which acts on a repository, not on an environment) is served only by a launcher configured to serve such orders; who runs them is SPEC Q-26.
7. **Where the launcher gets what its checks read, how time and delivery keep an in-memory ledger sound, and a first slice that is a dry run** are a **proposal of their own, [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md)** (status: proposed), so that what is accepted here stays separable from what is not.

## Alternatives considered

| Alternative | Why not |
|---|---|
| The launcher in C#, in its own project that references only Core | Simplest, the verifier is written once, but a .NET runtime in the most sensitive component, in an image far larger than needed. The maintainer prefers the small surface. Remains the fallback if the vectors prove too heavy to keep |
| Rust, with a verifier written independently and no shared files | Two opinions on the same bytes: a divergence is a security flaw nobody sees until it matters. Rejected |
| One Rust verifier called from the control plane through a native interface (FFI) | Only one implementation, but it puts a native build and an unsafe boundary in the central container and in every test of the control plane, for a function that signs on the other side and builds the bytes in C# anyway |
| A schema compiled to both languages (Protobuf and the like) | It shares the shape of the data, not the checks or their order; the order of the checks and what a decision spends are the risky part |

## Consequences

- The repository gains a second language: a Cargo project, a CI lane (`rustfmt`, `clippy` as errors, the tests, an audit of the dependencies), Dependabot entries, an image and its scan. Portarr's reader is the model to copy; [TESTING.md](../TESTING.md) says what runs where. The Cargo lane is a **required check** of the repository once the launcher exists (`required_checks` of the repository's file in `github-template`), so that a change to the rules cannot merge with the Rust side red.
- The supply chain of the binary that holds the workload identity is pinned: `Cargo.lock` committed, `cargo build --locked`, `cargo deny` (advisories, licences, sources limited to crates.io), the builder and base images pinned by digest, the CI actions pinned by SHA.
- The vectors are the reviewable form of the safety rule: a person (or an agent) can read the cases, with no need to read either implementation. A new refusal is a new vector.
- The C# `Launcher` and the Rust launcher are checked against each other through the files, not by one calling the other; either can be rewritten without the other as long as the files agree.
- Row 3a changes Core before any vector is generated (the issuer addresses an order to the launcher of its environment, the shared `"launcher"` audience default goes away, the checks of points 5 to 7 are added); [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md) lists it, and the tests of the proposal.
- Arrives with the vectors, and is tested then: the gitleaks allowance for the published test key (one value) and the scan of the images; that both suites read the same set of files; that every refusal code has a vector, the boundary values (`<` or `≤`) being written in the README of the vectors; that a changed expected outcome in a vector diff blocks the review; that Core's `Launcher` is registered by no host and referenced by no host project.
- The format of the vector files (one file per case, JSON, a version field, decision and signature vectors apart) is fixed when they are written, in `contract/launch/README.md`.
- The vertical that validates and merges is split: the launcher and the vectors first (SPEC roadmap row 3a), the merge after (row 3b).

## Not decided here

- When the order is signed relative to the approval, and what happens when a production step-up has gone stale or the order has expired before the launcher pulls it (the issuer's trigger and the re-issue): settled with row 3a, with the open question of [the security model](../security-model.md) on the refresh of the operator's session.
- Who runs an order with no environment, a merge only (SPEC Q-26).
- Mutation testing of the Rust launcher (the .NET lane uses Stryker); it follows when the launcher exists.
- The launcher stops being a few hundred lines once it carries an HTTP client, JSON and Ed25519; the bound is the handful of crates, listed in the pull request that writes it.
- How the launcher authenticates to the control plane's API (SPEC Q-25).
- The Rust crates beyond the standard library (the signature check, the HTTP client, the JSON of the vectors) and the C# Ed25519 library: chosen with row 3a, kept to a handful and listed in the pull request.
- Where the launcher runs (SPEC Q-27).

See also [How an order is secured, from the person to the machine](../security-model.md).

# 0014 — The launcher is a Rust binary, held to the control plane by shared conformance vectors

- **Status**: accepted for the language and the vectors (the maintainer chose on 2026-11-04); proposed for how the launcher gets its orders (the safest default, to be changed if wanted)
- **Date**: 2026-11-04

## Context

[ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) and [ADR 0007](0007-a-dedicated-vault-and-a-small-control-plane.md) make the launcher a boundary of its own: first a module with an interface, then a separate process, then a separate host. They say it is "small and boring on purpose" and that it has no inbound port, but **they do not say in which language it is written**. Today it is C# library code in `Orchestrarr.Core` (`Launcher`, the order's canonical form, the refusal codes), tested and not run by any process.

Two facts weigh on the choice:

- **The launcher is the most sensitive component.** It holds the workload identity that Vault answers to (one per environment, [ADR 0012](0012-one-launcher-and-one-vault-role-per-environment.md)) and it is the only thing able to start an agent. What it carries is worth keeping small: no runtime, no shell, few dependencies.
- **The control plane is allowed to be heavy.** It is one central .NET container; the maintainer does not mind its weight. The launcher, one per environment, is the one that should be light. Portarr already made this trade for its machine-side probe (its ADR 0011: the reader, a Rust binary under 1 MB, in an image that holds nothing else), and Mnemarr's collector is also Rust.

Writing the launcher in a second language means **two implementations of the same security rule**: the canonical bytes that are signed, the checks and their order, the refusal codes. If they ever differ (a field written another way, a check moved, a clock tolerance applied to the other side), an order could pass in one and not in the other, or worse, mean something else. That is the real cost, and it is a security flaw if left to chance.

## Decision

1. **The launcher is a Rust binary**, in its own Cargo project (`launcher/`), built into its own image (a static binary, no shell, no libc, no runtime). It opens no port. It is not a dependency of the host and the host is not a dependency of it. The footprint is a goal, not a number to defend: the reader's order of magnitude (about 1 MB, a few MB of memory) is the reference.
2. **The contract between the two implementations is the signed order, written down once, in a language-neutral form**, and held by **shared conformance vectors**: data files, in `contract/launch/`, that both implementations must satisfy. The contract is:
   - the canonical form (`orchestrarr-order-v2`) and the signature scheme;
   - the checks the launcher makes, **in order**, and the refusal code of each (`LaunchRefusal`);
   - the defaults of the options (lifetime ceiling, clock tolerance, age of the second factor);
   - the audit entries (event name, subject, detail) that a decision writes, and what an unverified order is never allowed to write;
   - what a decision spends: an order refused before the last check does not burn its id; an order that passed every check but the final read of the stop stays spent.
3. **The vectors are produced by the control plane's side, in C#, and committed.** Each vector is one case: the fields of the order and its options, the state of the world the checks read (the stop engaged or not, production or not, powers granted or not, the id already used or not, the verifier available or not, the time), the canonical bytes in hexadecimal, the signature, and the expected outcome (the refusal code, or accepted with the canonical bytes of the **normalized** order that is to be run, and the audit entries written, and whether the id was spent). There is a vector per refusal, one for the accepted order, and the awkward ones: absence against the empty string, steps unsorted or repeated, times with fractions, a field that moves a boundary, a signature of another version, an order of another form.
4. **Both suites run all the vectors.** The .NET tests regenerate the vectors and fail when the committed files differ (so a change to the form cannot go in without the files); they also check that the C# launcher gives the expected outcome. The Rust tests read the same files and check that the Rust launcher gives the same outcome, byte for byte on the canonical form. **A change to the form, to a check or its order, to a refusal code or to a default is one pull request that changes the C# code, the Rust code and the vectors together**; a change of the wire form also changes the tag. The C# `Launcher` stays in Core as the executable reference and as the oracle of the vectors; it runs in no process.
5. **The signature scheme is asymmetric, and the vectors sign with Ed25519**, with a key pair made for the tests and published in the vectors, in the form Vault's transit engine gives (`vault:vN:` and the signature). Ed25519 is deterministic (the same bytes and key give the same signature, so the files do not change on every run) and Vault's transit engine offers it. The production verifier (a public key, the key version bound) comes with the Vault adapter ([SPEC](../SPEC.md) Q-23).
6. **What the vectors do not cover is deliberately left out of the contract**: the launcher's adapters (how it gets an order, how it talks to Vault, how it starts the workshop) are tested on their own, in Rust, with fakes. The vectors hold the pure decision: an order, a signature and a snapshot of what the checks read, in; a decision and its trace, out.
7. **How the launcher gets its orders** (proposed default): it **pulls them through the control plane's API**, which is the only reader of the store (the file of [ADR 0013](0013-approvals-the-trail-and-the-stop-are-kept-in-a-sqlite-file.md) is a single-replica local store: the launcher never opens it). The launcher polls, it opens no port (SPEC D-46). How it authenticates to that API is open (SPEC Q-25).
8. **The first slice that runs the Rust launcher is a dry run**: it pulls, verifies, writes to the audit trail what it would have started, and starts nothing; it holds no credential to Vault, no access to a forge and no container runtime. It proves the chain (the order, the signature, the checks, the pull, the trace) before any power exists.

## Alternatives considered

| Alternative | Why not |
|---|---|
| The launcher in C#, in its own project that references only Core | Simplest, the verifier is written once, but a .NET runtime in the most sensitive component, in an image far larger than needed. The maintainer prefers the small surface. Remains the fallback if the vectors prove too heavy to keep |
| Rust, with a verifier written independently and no shared files | Two opinions on the same bytes: a divergence is a security flaw nobody sees until it matters. Rejected |
| One Rust verifier called from the control plane through a native interface (FFI) | Only one implementation, but it puts a native build and an unsafe boundary in the central container and in every test of the control plane, for a function that signs on the other side and builds the bytes in C# anyway |
| A schema compiled to both languages (Protobuf and the like) | It shares the shape of the data, not the checks or their order; the order of the checks and what a decision spends are the risky part |

## Consequences

- The repository gains a second language: a Cargo project, a CI lane (`rustfmt`, `clippy` as errors, the tests, an audit of the dependencies), Dependabot entries, an image and its scan. Portarr's reader is the model to copy; [TESTING.md](../TESTING.md) says what runs where.
- The vectors are the reviewable form of the safety rule: a person (or an agent) can read the cases, with no need to read either implementation. A new refusal is a new vector.
- The C# `Launcher` and the Rust launcher are checked against each other through the files, not by one calling the other; either can be rewritten without the other as long as the files agree.
- Mutation testing of the Rust launcher is not decided here (the .NET lane uses Stryker); it follows when the launcher exists.
- The vertical that validates and merges is split: the launcher and the vectors first (SPEC roadmap row 3a), the merge after (row 3b).

## Not decided here

- How the launcher authenticates to the control plane's API, and the control plane to the launcher (SPEC Q-25): a token of the launcher's workload identity, mutual TLS, or the identity provider's service accounts.
- The Rust crates beyond the standard library (the signature check, the HTTP client, the JSON of the vectors): chosen when the launcher is written, kept to a handful and listed in the pull request.
- Whether the launcher runs in the production cluster or on the workshop host (ADR 0012 only says one per environment).

See also [How an order is secured, from the person to the machine](../security-model.md).

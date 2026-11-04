# 0016 — The launcher acts under a mandate of its own: a signed token per launcher, and the launcher of the home environment serves orders with no environment

- **Status**: accepted by the maintainer on 2026-11-04 (nothing is built: the launcher is row 3a of the [SPEC](../SPEC.md) roadmap; the signed stop statement and the machine routes stay proposals, [ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md) and SPEC Q-27)
- **Date**: 2026-11-04

## Context

[ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md) has the launcher pull its orders through the control plane's API and leaves two points open: how the launcher authenticates to that API and knows it is the real one (SPEC Q-25), and who runs an order that has no environment, which is every step that can be approved today, since a merge acts on a repository through the forge, not on an environment (SPEC Q-26). Both are needed before the first slice of the launcher, the dry run.

The principle the maintainer set: **the action is always delegated to a launcher, and a launcher can act only under a real mandate.** A launcher acts on the environment where it runs, and it reaches outward (the forge, another site) only through connections it opens itself.

## Decision

1. **The launcher authenticates to the control plane's API with a signed token of the identity provider, one client per launcher.**
   - The flow is the OAuth 2.0 client credentials grant with a signed assertion (`private_key_jwt`). The key pair is generated where the launcher runs, and **the private key never leaves it**; the public key is registered with the identity provider for a client of its own, **by a person or by the identity provider's own infrastructure-as-code, never at run time by the launcher or the control plane**: neither holds a credential that manages the identity provider's clients, so that one compromise cannot become the identity of every launcher (proposed constraint). The private key is a secret mounted where the launcher runs, not state it writes (the clause of ADR 0014 on state on disk is about what the launcher writes). There is one client per launcher, so one per environment (D-55), which is what ADR 0015 requires: an identity stable per launcher and distinct per environment.
   - The token lives for minutes. Its audience is the control plane's API. It carries a `launcher` role that **can never be an operator** (D-47) and reaches only the routes for machines (delivery, decision, stop statement, published grants, audit; their detail is SPEC Q-27). A person's session cannot use the routes for machines, and a launcher's token cannot use the routes for people.
   - **Revoking a launcher is disabling its client**: its token expires within minutes and no new one is issued.
   - The launcher knows the API is the real one through **TLS verified against the certificate authority it is configured with**; plain HTTP is refused. Mutual TLS is not used: the signed token replaces it, and the signed stop statement of ADR 0015 §5 (proposed, SPEC D-60) is what keeps a false API from saying the stop is released; it does not depend on this decision. "Authenticated in both directions" in ADR 0015 reads as this.
   - The token is a mandate to **ask for the orders addressed to its own environment**, not a power. The launcher still verifies each order's signature and its own grants and production flags; the token alone starts nothing.
2. **The token gives no access to Vault.** A token of the identity provider gives no access to Vault. To act, the launcher asks Vault for credentials, for one run and one environment, under its workload identity ([ADR 0012](0012-one-launcher-and-one-vault-role-per-environment.md)). Whether that identity is the same client of the identity provider (Vault accepting its signed token) is settled with Vault's configuration (D-44), not here. The dry run holds no credential to Vault.
3. **The launcher of the environment where the control plane is installed (the home environment) also serves the orders that have no environment**, that is the work done through the forge: validating or merging a pull request.
   - It lists "no environment" among what it serves (ADR 0014 point 6), as an entry of its configuration with **its own grants, its own production flag and its own forge credential**, a GitHub App token for one repository from a Vault role of its own, kept apart from the roles that act on the environment. No other launcher serves such orders, and a launcher serves them only if its configuration says so.
   - This **amends D-55** (one launcher per environment): the home environment's launcher serves two things, the environment and the forge. It replaces the launcher of its own for the forge work that SPEC Q-26 proposed, so there is one kind of launcher.
   - **Whether such an order is production is decided per repository, not once for all** (proposed default, to be confirmed by the maintainer): a merge deploys when its repository is deployed by continuous deployment or by GitOps, so the launcher's configuration says, for each repository it may act on, whether it is production, and a repository that is not listed is production (D-17: unknown is production). The issuer classifies the same way from its own configuration, so that the step-up of D-51 applies to such a merge. The vectors cover an order with no environment on a repository marked production, on an unlisted one and on one marked not production.
   - **The home launcher runs in a namespace of its own, with a service account of its own** (proposed requirement): the control plane's identity has no right there (its service account, its key, its claim), and the forge's Vault role is bound to the launcher's workload identity only, never to the control plane's, so that a compromised control plane cannot both sign and execute (D-45).
   - A task on a remote environment is run by the launcher of that environment, which opens its connections outward only; the home launcher does not reach into it.

## Alternatives considered

- **The workload token of the cluster (a Kubernetes service account)**: elegant, but it works only for a launcher inside that cluster, so a launcher on another host or site would need a second mechanism.
- **Mutual TLS**: it adds the life of certificates (issue, renewal, revocation) to a product that means to stay light; the signed token covers the identity, and TLS covers the API's.
- **A shared secret** (a client secret): it exists on both sides and travels; a key pair that never leaves the launcher is stronger for the same effort.
- **A launcher of its own for the forge work** (SPEC Q-26's proposal): a second kind of launcher to run; rejected by the maintainer in favour of the home environment's launcher.

## Consequences

- The identity provider gains a client per launcher, with a `launcher` role and a short token lifetime. If it is unreachable, no launcher can renew its token, so nothing starts: a failure that stops, which D-43 accepts.
- The home environment's launcher holds more than another: a compromise of it yields the home environment and the forge for the repositories its forge role covers, and no other environment. The two entries of its configuration, and the two Vault roles, are the separation.
- SPEC Q-12 (keys for machines) is settled for the launcher, and Q-25 and Q-26 are resolved. The machine routes stay open (Q-27), and so does the rotation of a launcher's key.
- Tested with row 3a: that a token without the `launcher` role, or one of a person, is refused on the routes for machines and the reverse; that an expired token or one for another audience is refused; that a launcher's token reaches only the order delivered to its own identity; that an order with no environment is delivered to the home environment's launcher only; and, in the launcher, that it refuses to start without its key, with a plain HTTP address, or without the certificate authority it is configured with.

## Not decided here

- The rotation of a launcher's key (how often, and how the identity provider learns the new public key), and where the key is kept where the launcher runs: settled with row 3a (SPEC Q-27).
- The machine routes themselves, the stop statement, and where the launcher runs ([ADR 0015](0015-where-the-launcher-gets-what-its-checks-read.md), SPEC Q-27).
- Whether Vault authenticates the launcher with the same client as the API (point 2), and the confirmation of the proposed defaults marked above (the production classification of an order with no environment, the registration of the public key, the namespace of the home launcher).

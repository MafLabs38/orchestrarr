# 0007 — A dedicated Vault tied to the identity provider, and a small control plane that only launches

- **Status**: proposed (how clients reach the Vault, and over what, is open, SPEC Q-23)
- **Date**: 2026-11-03

## Context

[ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) puts the security of an omnipotent agent in **who can cause a launch**. It needs an authority that signs orders, issues short-lived credentials and keeps the few long-lived secrets (the model credential, the forge application key). The maintainer already runs Vault instances for other purposes, and an identity provider (Keycloak). Reusing one of those Vaults would tie the keys that let agents act on production to a system with another owner, another lifecycle and another health (one of them is sealed today).

## Decision (proposed)

### The Vault

1. **A new Vault, dedicated to this purpose.** The existing Vaults stay as they are: no shared storage, no shared unseal keys, no shared policy or identity. A compromise of one must not reach the other.
2. **Deployed by its own infrastructure-as-code repository, outside Orchestrarr.** Orchestrarr consumes the Vault, it does not provision it. Two layers, as in the identity provider's repository: a **platform** layer (the host, the Vault, its initialisation and its unseal) and a **configuration** layer (authentication methods, secret engines, policies, identities). The configuration layer is applied with a short-lived credential, never with the root token, which is revoked after the bootstrap.
3. **Tied to the identity provider.** People sign in to Vault through its OpenID Connect method; a group of the identity provider maps to a policy (`operator`, `admin`), so that the same roles govern the product and the Vault. Machines never use a person's login.
4. **One identity per component, one narrow policy each.** The split is deliberate: no single compromised component can both sign an order and obey it.

   | Identity | Can | Cannot |
   |---|---|---|
   | `control-plane` | **Sign** an order with a non-exportable key of the transit engine | Read the key, issue any credential, reach a machine |
   | `launcher` | **Verify** an order's signature, **request** the credentials an order names (SSH certificate, forge application token, the model credential), each for one run | Sign an order, read other secrets, list or widen its own policy |
   | `agent-run` | Nothing in Vault: it is handed what its run needs, for the length of the run | Authenticate to Vault, start a run |
   | `admin` (a person, step-up) | Change policies and identities | Be used by a machine |

5. **Engines**: transit (the order-signing key, never exportable), the SSH certificate authority (the machines accept only its certificates for the operator principal, with a maximum time to live), a key-value store (the model credential, the forge application key, nothing else) and an **audit device** whose log is shipped off the host.
6. **Sealing is the deepest kill switch.** A sealed Vault signs and issues nothing, so every launch fails closed. Restarting the Vault therefore needs a deliberate unseal; automatic unseal against another Vault would tie this one to it and is not wanted by default (Q-22).

### The control plane

The product is split as Kubernetes splits its own, and for the same reason: the part that decides is not the part that executes.

| Part | Role | Holds | Listens |
|---|---|---|---|
| **API and store** (Orchestrarr's host) | Authenticates people, records decisions, builds and signs orders, keeps the audit trail | The right to sign (transit), no credential that acts | Yes, behind the identity provider |
| **Launcher** | The only thing able to start an agent run: verifies an order, requests credentials, starts the run, ends it | The right to verify and to request credentials, no right to sign | **No.** It opens no port: it **pulls** signed orders from the store |
| **Agent run** | Acts on one environment, as Claude Code would | Credentials of one run and one environment | No |

The design rules:

1. **An attacker cannot call the launcher**, because it has no inbound endpoint. It only reads orders, and only an order signed by the API's key, still valid and unused, makes it do anything.
2. **Whoever gives an order is authorized twice.** The API checks the identity provider's token (operator role, and the step-up claim for production) before signing; the order carries the approver's identity; the launcher checks that the signature is valid, that the order is not expired or replayed, and that what it asks is inside the powers granted (SPEC D-10, D-11) and the budget.
3. **Only a person approves.** A service client of the identity provider cannot hold the operator role, so a token stolen from a machine cannot produce an order. The agent has no path to the API's approval route.
4. **The launcher is small and boring on purpose**: a deterministic loop, no model in it, no template engine, a few hundred lines that can be read in one sitting. It is a module with an interface first, then a separate process, then a separate host, as ADR 0006 planned.
5. **Fail closed** (SPEC D-43): no audit sink, a sealed Vault, an unreachable store or an engaged kill switch means no launch.

### Where it runs

On the production cluster, like every production element (SPEC D-48): a one-node StatefulSet in its own namespace, integrated storage on a retained volume, deployed by the cluster's GitOps repository, configured by the Vault's own repository, with no Kubernetes API token in the pod and network policies that close everything by default. It starts sealed, and the unseal stays a deliberate act.

The cost is accepted and written down: the root of trust lives in the cluster the agent may act on, on a single node. Two answers: the credential issued to a run for the cluster **excludes the Vault's namespace** (no `exec`, `port-forward`, `secrets` or logs there), and a sealed Vault protects its data at rest whatever happens to the cluster. A cluster administrator could still read the memory of an unsealed Vault, which is why the administrator role is never given to a run.

## Not decided here

- How clients reach the Vault and over what (SPEC Q-23): TLS, and a way in for a launcher outside the cluster.
- Whether the launcher authenticates to Vault with an AppRole or with a workload identity of its host.
- Whether production needs one approver with a step-up, or two (SPEC Q-21).

## Consequences

- A new repository and a new virtual machine to maintain, in exchange for a root of trust that does not depend on any other system's health.
- Orchestrarr needs a store of orders the launcher can poll, a signing client and a verifying client for Vault: provider interfaces first, an in-memory fake for tests, so nothing here needs a real Vault until the vertical that acts.
- The first vertical that acts (validate and merge) needs the order, the approver's identity and the audit trail, but not yet the SSH authority.

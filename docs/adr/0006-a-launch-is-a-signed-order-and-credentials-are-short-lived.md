# 0006 — A launch is a signed order, and credentials are short-lived

- **Status**: proposed (needs the maintainer's decision)
- **Date**: 2026-11-03

## Context

The agent that acts on an environment is omnipotent there, on purpose: it runs with high rights on production virtual machines, as the maintainer's other automations do, and it never runs on the maintainer's own machine (ADR 0004, revision). Limiting what it can do would defeat its purpose.

So the security of the product sits elsewhere: **in who can cause a launch, and in what a launch is bound to.** An omnipotent agent that anyone, or anything, can start is the real danger. Keycloak (identity) and Vault (secrets, service accounts) exist in the maintainer's infrastructure; The Vaults that exist serve other purposes and one is unhealthy: ADR 0007 provisions a dedicated one.

## Decision (proposed)

1. **Three parts that do not trust each other more than needed.**
   - The **control plane** (Orchestrarr's API and screens): decides, records approvals, keeps the audit trail. It holds **no credential that acts** on an environment.
   - The **launcher**: a small deterministic component and the **only** one that can start an agent run. It starts one from a **signed order**, nothing else.
   - The **agent run**: omnipotent on the environment it was started for, for the duration of the run. It has no way to start a run, to sign an order or to reach the launcher.
2. **Who gives the order is authenticated by the identity provider (Keycloak in the reference setup).** The interface and the API are behind OpenID Connect. Approving needs the *operator* role. Approving a work item that touches a production environment needs a **step-up** (a fresh second factor at the moment of the approval). A mail link opens the decision card, it never acts (SPEC D-12).
3. **A launch is a signed, single-use, time-limited order built by the server**, never a request that carries a command or a prompt. It contains the work item, the steps the operator ticked, the repository and its head commit, the environment, the approver's identity, an issue time, an expiry and a nonce. The launcher checks the signature, the expiry, that the nonce was never used, that the steps and the environment are within the powers granted (SPEC D-10, D-11), the concurrency and the budget, and that the kill switch is not engaged. No endpoint of the product accepts a free command or a free prompt: the instruction of the agent is built from the repository and from templates, and what findings say is data.
4. **No standing credential anywhere.** At launch, Vault (or, more simply, an SSH certificate authority) issues credentials **for this run and this environment**, valid for the length of the run: an SSH certificate for the operator account of the machine, a short-lived token for a cluster, a GitHub App installation token scoped to one repository, and the model credential (the subscription token), which stays in Vault and is handed to the run only. The launcher authenticates to Vault with its own identity, and its policy only lets it request what a valid order names.
5. **The machines only accept what the authority signed.** On each environment, the secure shell accepts certificates of that authority for the operator principal, from the launcher, and no password and no standing key. The operator account has the rights its projects need (high ones, accepted). Whoever steals a session of the interface still cannot get on a machine without an order.
6. **Everything is recorded, and the product fails closed.** The order, its approver, the credentials issued (Vault's audit device), the commands run (command logging on the machine, shipped as it goes), the outputs and the final state go to Orchestrarr's own store, and into a journal folder of the environment (SPEC D-35). If the audit sink is unavailable, no order is accepted.
7. **The kill switch stops the launches and the runs**: the launcher stops accepting orders, the signing authority stops signing, and the active leases are revoked, which ends the running sessions.

### The signed form (canonical form v2)

The signature covers bytes, and the launcher acts on what those bytes say, so the form leaves no room: a format tag (`orchestrarr-order-v2`), then every field written with a presence byte and a length (so a value can never move a boundary, and no value differs from absence only by being empty), the steps sorted and without repeats, times in whole seconds. Every field is also validated by shape before signing and again after verifying (the full commit, `owner/name`, a restricted alphabet and a length cap), because the fields come from findings and requests. The launcher answers with the **verified, normalized order**, and only that is run. The signature scheme must be asymmetric: the verifier holds a public key, so the launcher cannot forge an order. A change of form changes the tag.

## Threats and what answers them

| Threat | Answer |
|---|---|
| Someone gets the maintainer's session in the interface | The step-up on production; a mail link never acts; the approval is audited and alerts |
| Someone calls the API directly | OpenID Connect for every call that changes something; machine clients have their own identity and no operator role |
| Text planted in a finding, a changelog, an issue or a log steers the agent | The instruction is built by the server; the text is data; the agent is bounded to the environment and the plan of the order; the journal shows what it did |
| The agent tries to start another run, or to widen its own powers | It has no path to the launcher and no signing key; its credentials expire with the run |
| A stolen credential | It lives for one run and one environment; there is no standing key to steal |
| The control plane is compromised | It is the root of trust, so it is kept small, behind the identity provider, never an agent host; the signing key is in Vault and the machines trust the authority, not the control plane |

## Not decided here

- The authority that issues the credentials is a new Vault dedicated to this purpose ([ADR 0007](0007-a-dedicated-vault-and-a-small-control-plane.md), SPEC Q-20).
- Whether production needs one approver with a step-up, or two people (SPEC Q-21).

## Consequences

- The launcher is a boundary of its own: first a module with an interface, later a separate process, then a separate host.
- A healthy Vault, or an SSH authority, and a configuration of the secure shell on each machine (done as code, through the infrastructure repositories) are prerequisites of the first act on an environment.
- The first verticals that act (validate and merge, then deploy) do not need all of it: they need the order, the approver's identity and the audit trail; the machines' side comes with the vertical that acts on machines.

# 0011 — People sign in through the identity provider; the host keeps the session

- **Status**: proposed
- **Date**: 2026-11-03

## Context

[ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) puts the security of an agent that can act in **who can cause a launch**, and says the interface and the API are behind OpenID Connect (SPEC D-40, D-47). [ADR 0010](0010-approving-records-a-decision-and-the-sandbox-operator.md) built the approval with a **sandbox operator** that has no sign-in and that the host refuses outside the Development environment and off the loopback, and listed the sign-in (SPEC Q-12) as a condition before a launcher exists. Until now the API had no authentication at all: the image had to sit behind an authenticating front, which only protects what the front covers.

## Decision (proposed)

1. **The host signs people in itself**, through the authorization code flow with PKCE, as a confidential client of the identity provider (`Approvals:Mode=OpenIdConnect`, section `SignIn`). It does not trust a header that a front adds, and it does not depend on the network around it.
2. **The browser never holds a token.** The host keeps the claims of the identity token in a session cookie (`HttpOnly`, `SameSite=Lax`, `Secure` and named `__Host-orchestrarr.session` as soon as the public address is https, so that a sibling site of a shared domain cannot plant its own session; a fixed lifetime of `SignIn:SessionHours` that use does not extend). Nothing else is kept: no access token, no refresh token. The claim that says a second factor was passed (`acr`), which the handler drops by default, is kept on purpose.
3. **Everything the API says is for a person who signed in**, reads included, because who approved what is not for anybody. A request without a session is a 401 and never a redirect to the provider; a page of the interface is sent to sign in by the interface itself, which comes back to where it was. The files of the interface are public (they say nothing about the fleet), and so are the sign-in's own routes and `GET /health`.
4. **An operator is a member of a configured group** (`SignIn:OperatorGroup`, read in the claim `SignIn:GroupsClaim`). A group is compared **exactly as the claim writes it**: with Keycloak's full path, `/orchestrarr-operators`, so that a group of the same name in another branch of the tree is not an operator. The subject of an approval is the identifier the identity provider gives (`sub`), which does not change when the person is renamed; the name shown is for display only. Being signed in is not being an operator: somebody who is not one reads, and the approval and the stop are refused with `notAnOperator`, and the interface does not offer them.
5. **Who may read is a choice**: with `SignIn:ReaderGroup` only its members and the operators read; **without one, anybody who signed in reads, which is every account of the provider's realm**. In a shared realm (family, guests, other applications), set it.
6. **The second factor is read, not asked for yet.** When `SignIn:StepUpAcr` is set and the session was opened at that authentication context, the approver carries the moment of it (`auth_time`); otherwise nobody counts as having passed one. Asking for a step-up (sending the person back with `acr_values`) comes with the first production approval.
7. **The addresses come from the configuration, never from the request**: the address the provider sends the code to, the one it sends the person back to after signing out, and the only origin a change may come from. A forged `Host` or `X-Forwarded-Host` changes none of them.
8. **A change must come from the application's own origin.** On top of `SameSite=Lax`, a request that is not a read is refused (`originNotAllowed`, 403) when its `Origin` is not the public one; when it has none, when `Sec-Fetch-Site` says another site; and when it has neither but carries the session cookie. Signing out is a POST, sent as a form, and the provider's own front-channel logout is not used.
9. **The host refuses to start unless it is safe**: the provider's address and the public address are https (http only on the loopback of a Development machine), the client and its secret are set, and the groups, the claim and the lifetime make sense. The secret never goes in a file that is committed. The host reads them into one validated, immutable value.
10. **The sandbox operator stays for the sandbox.** `Development` is unchanged (no sign-in, loopback only). A local identity provider for the demonstration is in `samples/keycloak`.

## Consequences

- A production host has one way to know who approves, and it is the identity provider's. The sandbox operator remains impossible there.
- **The session is a snapshot, and it is not revoked.** The groups are read at sign-in and kept for up to `SignIn:SessionHours` (8 by default): an operator removed from the group, or disabled, still approves and releases the global stop until their session ends; a copied cookie stays valid after the person signs out. Today that gives no power, since nothing runs. Before a launcher exists, the vertical that issues orders must close it, with a server-side session store that can revoke, or a re-check with the identity provider when the order is issued (ADR 0010, conditions). The role is therefore not "checked again with the identity provider" yet, which D-47 and ADR 0010 say the issuer will do.
- The session is encrypted with the host's data-protection keys. Without `SignIn:KeysDirectory` the host uses the framework's default (a folder of the user's profile when there is one, otherwise memory, which is the case of the image): a restart signs everybody out and a second replica cannot read the first one's cookies, and outside Development the host warns of it. With it, the keys sit on that volume **unencrypted**: whoever reads it can forge a session, so the volume is a secret. Encrypting them with the Vault belongs to the control-plane vertical.
- Signing out ends the session of the application and sends the person to the provider's end-session page. The provider asks for a confirmation because no token is kept to hint who leaves; that is the price of keeping none.
- The trail and the queue show the subject of the provider (an identifier), not a name. Names come with the persistent store (ADR 0010).
- A service account cannot sign in this way, so it cannot be an operator (SPEC D-47). Nothing for machines exists yet: nothing needs it.
- Signing in, signing out and a failed sign-in are logged by the host, not yet recorded in the audit trail.
- To sign in and read without approving anything, leave `Powers:Grants` empty: nothing is granted, so nothing can be approved. With the default `Disabled` mode the API stays open, as the README says.

## Not decided here

- How production approval asks for the step-up, which authentication contexts count (a list: providers use levels or URIs), and how long it stays fresh (SPEC Q-21).
- Recording the issuer of the identity (`iss`) with the subject, before the persistent store fixes its schema: a subject is only unique within one provider.
- Sign-ins and sign-outs in the audit trail.
- Access for machines to the API (keys, or the client-credentials flow), and the link in a mail: it opens the page, which signs in first.
- A shared session store, if the host ever runs as several replicas.

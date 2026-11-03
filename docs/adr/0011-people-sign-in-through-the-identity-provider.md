# 0011 — People sign in through the identity provider; the host keeps the session

- **Status**: proposed
- **Date**: 2026-11-03

## Context

[ADR 0006](0006-a-launch-is-a-signed-order-and-credentials-are-short-lived.md) puts the security of an agent that can act in **who can cause a launch**, and says the interface and the API are behind OpenID Connect (SPEC D-40, D-47). [ADR 0010](0010-approving-records-a-decision-and-the-sandbox-operator.md) built the approval with a **sandbox operator** that has no sign-in and that the host refuses outside the Development environment and off the loopback, and listed the sign-in (SPEC Q-12) as a condition before a launcher exists. Until now the API had no authentication at all: the image had to sit behind an authenticating front, which only protects what the front covers.

## Decision (proposed)

1. **The host signs people in itself**, through the authorization code flow with PKCE, as a confidential client of the identity provider (`Approvals:Mode=OpenIdConnect`, section `SignIn`). It does not trust a header that a front adds, and it does not depend on the network around it.
2. **The browser never holds a token.** The host keeps the claims of the identity token in a session cookie (`HttpOnly`, `SameSite=Lax`, `Secure` as soon as the public address is https, a fixed lifetime of `SignIn:SessionHours` that use does not extend). Nothing else is kept: no access token, no refresh token.
3. **Everything the API says is for a person who signed in**: reads included, because who approved what is not for anybody. A request without a session is a 401 and never a redirect to the provider; a page of the interface is sent to sign in by the interface itself, which comes back to where it was. With the sign-in on, no route answers anonymously except the sign-in's own.
4. **An operator is a member of a configured group** (`SignIn:OperatorGroup`, read in the claim `SignIn:GroupsClaim`). The subject of an approval is the identifier the identity provider gives (`sub`), which does not change when the person is renamed; the name shown is for display only. Being signed in is not being an operator: somebody who is not one reads, and the approval and the stop are refused with `notAnOperator`.
5. **The second factor is read, not asked for yet.** When `SignIn:StepUpAcr` is set and the session was opened at that authentication context, the approver carries the moment of it (`auth_time`); otherwise nobody counts as having passed one. Asking for a step-up (sending the person back with `acr_values`) comes with the first production approval.
6. **The addresses come from the configuration, never from the request**: the address the provider sends the code to, the one it sends the person back to after signing out, and the only origin a change may come from. A forged `Host` or `X-Forwarded-Host` changes none of them.
7. **A change must come from the application's own origin.** On top of `SameSite=Lax`, a request that is not a read is refused (`originNotAllowed`, 403) when its `Origin` is not the public one, or when the browser says it comes from another site (`Sec-Fetch-Site`). Signing out is a POST, sent as a form.
8. **The host refuses to start unless it is safe**: the provider's address and the public address are https (http only on the loopback of a Development machine), the client and its secret are set, and the group, the claim and the lifetime make sense. The secret never goes in a file that is committed.
9. **The sandbox operator stays for the sandbox.** `Development` is unchanged (no sign-in, loopback only). A local identity provider for the demonstration is in `samples/keycloak`.

## Consequences

- A production host has one way to know who approves, and it is the identity provider's. The sandbox operator remains impossible there.
- The session is encrypted with the host's data-protection keys. Without `SignIn:KeysDirectory` they live in memory, so a restart signs everybody out and a second replica cannot read the first one's cookies: a production deployment keeps them in a volume.
- Signing out ends the session of the application and sends the person to the provider's end-session page. The provider asks for a confirmation because no token is kept to hint who leaves; that is the price of keeping none.
- The trail and the queue show the subject of the provider (an identifier), not a name. Names come with the persistent store (ADR 0010).
- A service account cannot sign in this way, so it cannot be an operator (SPEC D-47). Nothing for machines exists yet: nothing needs it.

## Not decided here

- How a production approval asks for the step-up, and how long it stays fresh (SPEC Q-21).
- Access for machines to the API (keys, or the client-credentials flow), and the link in a mail: it opens the page, which signs in first.
- A shared session store, if the host ever runs as several replicas.

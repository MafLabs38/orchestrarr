# 0018 — People sign in with local accounts and permission-based roles

- **Status**: accepted by the maintainer on 2026-11-10, as principles (built in the executor vertical's branch)
- **Date**: 2026-11-10
- **Spec**: D-66 (supersedes D-54, amends D-40)
- **Supersedes**: [ADR 0011](0011-people-sign-in-through-the-identity-provider.md)

## Context

ADR 0011 made the identity provider the only way in. The maintainer tried it and does not want it: the other two products of the constellation (Portarr, ADR 0009; Mnemarr, ADR 0016) have **local accounts, a cookie session and roles made of permissions**, and a self-hosted *arr-style container "must carry its own security". Orchestrarr was the odd one out, and a demo needed a whole identity provider. Since ADR 0017 the proof of an approval is the second factor that the executor checks, so the sign-in no longer is the root of trust of an approval: it tells who asks, the code proves it.

## Decision

1. **Local accounts with a cookie session**, as in Portarr and Mnemarr: a name and a password (ASP.NET Core's `PasswordHasher`, PBKDF2; a minimum length), a lockout after repeated failures (five in fifteen minutes, per name, the same answer for an unknown name and a wrong password, a decoy hash so that both take the same time), a bounded number of passwords hashed at once.
2. **Sessions are kept by the server** (a hash of the session id in the database): signing out, a new password, a disabled account and the end of the absolute lifetime end a session for real. The cookie is `HttpOnly`, `SameSite=Strict`, `Secure` with the `__Host-` prefix by default (`Auth:CookieSecurity=SameAsRequest` only for a plain-HTTP sandbox on a private network). A write from a person carries the custom header `X-Orchestrarr-Request` (CSRF), on top of `SameSite`.
3. **A role is a named set of permissions, and the permissions are a closed list in code.** One authorization policy per permission, declared on each route, and a **fallback policy that requires a sign-in everywhere else**: a route nobody thought about is closed. Roles: `Admin` (everything, accounts included), `Operator` (reads, approves, engages and releases the global stop), `Reader` (reads). The permissions are read from the database at every request: a changed role or a disabled account applies at once. The last enabled `Admin` can be neither deleted, disabled nor demoted.
4. **The first administrator** is created at start from `Auth:AdminUser` and a password read from the file `Auth:AdminPasswordFile`, only when there is no account. Break-glass: `Auth:ResetPasswordUser` and `Auth:ResetPasswordFile`, while both are set, reset that account at each start. There is no default account.
5. **The subject of an approval is the id of the account** (it never changes; a name can be reused). The executor's second factor is enrolled for that id.
6. **No sandbox operator and no mode that trusts the network** (`Approvals:Mode` is removed): every request needs a sign-in. Tests sign in through a test-only filter, and the real sign-in is tested through its own routes.
7. **Account events are in the audit trail** (sign-ins, changes of accounts and roles). The failures anybody can provoke have the smaller share of the bounded trail, like the refusals.
8. **An identity provider is a later, optional step**: a new authentication scheme only has to produce the same principal (the model leaves room for an external issuer and subject). The OpenID Connect code and the sample Keycloak are removed meanwhile.

## Consequences

- The caveat of ADR 0011 ("the session is a snapshot, and it is not revoked") is gone, and so is the condition of ADR 0010 that the issuer checks the role again: the role is read at every request.
- Orchestrarr holds password hashes: the database is a secret, and a backup of it too.
- One instance per database: the lockout counters live in memory.
- A service account (for the executor, or an assistant) is not part of this step: the executors keep their own token (ADR 0017).

# A local identity provider for the sandbox

A throwaway Keycloak to try the sign-in of Orchestrarr (SPEC D-54, ADR 0011). **Only for a development machine**: the users, their passwords and the client secret are in `realm-orchestrarr.json` in clear text, and the provider speaks http on the loopback.

```bash
docker compose up -d   # http://localhost:8081, administrator admin/admin
```

The realm `orchestrarr` has:

| What | Value |
|---|---|
| Client | `orchestrarr`, confidential, secret `sandbox-only-client-secret`, authorization code flow with PKCE (S256), redirect `http://localhost:5178/auth/callback` |
| Group | `orchestrarr-operators`, sent in the `groups` claim of the identity token |
| `alice` / `alice` | a member of the group: an operator |
| `bob` / `bob` | not a member: can read, cannot approve or stop |

Then start Orchestrarr with the sign-in on (see the README, "To try the sign-in") and open `http://localhost:5178`. Stop the provider with `docker compose down`; it keeps nothing.

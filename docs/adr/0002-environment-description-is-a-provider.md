# 0002 — The environment description is a provider; Portarr is the first one

- **Status**: accepted
- **Date**: 2026-11-01

## Context

Orchestrarr is a satellite of Portarr. Portarr's job is to describe environments and architecture: environments, runtimes, workloads, applications, the sources of their code and of their deployment. Orchestrarr must know where an application runs to know where to redeploy it, but it must not keep a second inventory.

## Decision

1. Applications **declare nothing** to Orchestrarr. How and where an application is deployed is **observed** through the environment description.
2. The environment description is read through a **provider**. **Portarr** (API / MCP) is the first and reference provider.
3. For standalone use, a second provider reads a **structure file** (YAML, in the spirit of infrastructure as code) that describes the same things: environments, workloads, applications, sources. Both providers fill the same internal model.
4. If Portarr does not expose something Orchestrarr needs (for example the exact workload that runs an application, or the deployment source), the fix is in **Portarr**, not in a private copy inside Orchestrarr.

## Consequences

- No application profile to maintain by hand in Orchestrarr, only the powers the operator grants (ADR 0001).
- The structure file format is designed once, as the contract of the provider, and tested against Portarr's own data.
- Orchestrarr stays useful without Portarr, but has less to work with.

*(2026-11-04: one exception, for the launcher only, which classifies production from its own configuration so that its authorization stays independent of the control plane, [ADR 0014](0014-the-launcher-is-a-rust-binary-held-to-the-control-plane-by-shared-vectors.md) point 7.)*

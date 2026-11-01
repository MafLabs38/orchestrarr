# The environment file (`orchestrarr.environment.yaml`)

> **Status**: proposed (SPEC D-18, Q-15). Subject to change until the first vertical reads it.
> All names and addresses are examples.

Orchestrarr needs to know **what runs where** to redeploy an application (SPEC D-17, D-18). It reads that from an *environment description provider*:

- **Portarr** (reference): Orchestrarr reads it through Portarr's API / MCP. Nothing to write.
- **Environment file** (standalone): this document. A YAML file that describes the same things.

The file is deliberately **a subset of Portarr's catalog** ([catalog.md](https://github.com/MafLabs38/portarr/blob/develop/docs/catalog.md)): the same keys (`environments`, `apps`, `workloads`, `repository`, `check`), so a Portarr catalog can be used as an environment file, and a file written here can later become a catalog. The only new key is `deployment`.

Applications declare nothing to Orchestrarr. The file describes the **fleet**, once, for whoever reads it.

## Example

```yaml
environments:
  - id: cluster
    name: Kubernetes cluster
    runtime: kubernetes
    kubernetes:
      server: "https://10.0.0.20:6443"
      tokenEnv: K8S_TOKEN              # the variable name, never the token
      caFile: cluster-ca.crt
  - id: server-1
    name: Server 1
    docker: { url: "http://10.0.0.10:2375" }

apps:
  - id: shop
    name: Shop
    repository: example/shop           # the code (owner/name)
    urls: [{ href: "https://shop.homelab.example" }]
    workloads:
      - { runtime: kubernetes@cluster, match: { namespace: shop, app: shop-web } }
    check:                             # the smoke test (same as Portarr's)
      url: "https://shop.homelab.example/health"
      expect: [200]
    deployment:                        # NEW: how the workload is changed
      repository: example/infra        # where the manifests live
      path: apps/shop
      tool: kubectl                    # kubectl | helm | compose | terraform | ansible | release

  - id: recipes
    name: Recipes
    repository: example/recipes
    workloads:
      - { runtime: docker@server-1, match: { name: recipes } }
    deployment: { repository: example/recipes, path: compose.yaml, tool: compose }

  - id: notes-desktop
    name: Notes desktop client
    type: desktop                      # no workload: it runs on the users' machines
    repository: example/notes-desktop
    deployment: { tool: release, workflow: release.yml }   # publishing a release; clients auto-update
```

## What `deployment` means

| Key | Meaning |
|---|---|
| `tool` | `kubectl`, `helm`, `compose`, `terraform`, `ansible` or `release` (publish a release of a desktop client or a library) |
| `repository`, `path` | Where the deployment definition lives. Omitted when the application's own repository holds it, or when `tool` is `release` |
| `workflow` | For `release`: the CI workflow that builds and publishes |

Powers are **not** in this file. What Orchestrarr is allowed to do per application and environment is set in Orchestrarr's own settings (SPEC D-10, D-11), so the description can be shared with Portarr unchanged.

## How Orchestrarr derives the steps 🟡

What it does follows from the **runtime** of the workload and the `deployment.tool`. Nothing is declared by the application.

| Runtime / tool | Deploy | Roll back | Smoke test |
|---|---|---|---|
| `kubernetes` | Change the image in the deployment definition (or `kubectl set image`), wait for the rollout | Roll the workload back to the previous revision | Rollout complete, then the app's `check` |
| `docker`, `compose` | Pull the new image, recreate the container | Recreate with the previous image, which is kept | Container healthy, then the app's `check` |
| `release` (desktop client) | Run the release workflow | **None once clients updated** (SPEC D-13): every check happens before; fixing means a new release | Build and launch checks in CI, before publishing |

An application with no `check` has no smoke test: its deployment step is not offered on the card (SPEC D-10), only the merge.

## Rules

- **Never a secret**: tokens are named by environment variable (`tokenEnv`), as in Portarr.
- **A broken file keeps the last valid description**, and the error is shown (like Portarr's catalog).
- **If Portarr is configured, the file is ignored.** One source of truth at a time (a merge of both sources is not planned).
- If the file needs a field Portarr's catalog lacks (today: `deployment`), the field is added to Portarr as well (ADR 0002, point 4).

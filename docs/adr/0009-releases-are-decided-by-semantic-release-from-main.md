# 0009 — Releases are decided by semantic-release from main, which takes merge commits only

- **Status**: proposed
- **Date**: 2026-11-03

## Context

The three products share their conventions, and Portarr already releases this way ([its ADR 0014](https://github.com/MafLabs38/portarr/blob/main/docs/adr/0014-releases-with-semantic-release.md)). Orchestrarr publishes an image per branch and per commit (`develop`, `main`), versioned `0.0.0-<tag>`: no version, no tag, no release notes, and nothing for the cluster's GitOps to follow.

Constraints:

- `main` is protected: no direct push, even for the pipeline. Whatever releases cannot commit a changelog or a version file back to `main`.
- Pull requests squash-merge into `develop`, so each commit on `develop` carries one Conventional Commit message.
- The product is below 1.0: the first release is **0.1.0**, and an incompatible change must not jump to 1.0.0 by accident.
- The cluster deploys by GitOps (Flux), and the product holds no write power yet: nothing here is deployed with a power it should not have.

## Decision (proposed)

1. **semantic-release runs on `main` only**, as a job of the CI workflow after the tests, the scans and SonarQube (when it runs) have passed. It reads the Conventional Commits since the last `v*` tag, picks the next version, creates the tag and the **GitHub release**, whose notes are the changelog.
2. **Release rules** (`.releaserc.json`): `feat` is a minor, `fix`, `perf` and `revert` a patch, a breaking change is a **minor** while the product is below 1.0. Everything else does not release.
3. **The first release is 0.1.0**: a seed tag `v0.0.0` sits on the initial commit of `main`, and semantic-release counts from it.
4. **`main` takes merge commits only.** A release pull request `develop → main` is merged with a merge commit: a squash would turn the history into one message and hide every `feat` and `fix` from semantic-release (no version published). This is enforced in the repository's declaration (`merge_methods: [merge]` on the `main` ruleset, merge commits allowed on the repository), not by habit; squash stays the method for pull requests into `develop`.
5. **The version is `X.Y.Z` plus a build number**: the image is built with `VERSION=X.Y.Z.<run number>` and tagged `X.Y.Z` and `latest`. A run that releases nothing publishes the `main` and commit tags only.
6. **A run on `main` is never cancelled** by the next push: a tag created and an image not built is the one inconsistent state to avoid.
7. **The cluster deploys the newest version tag by GitOps**: Flux watches the image registry, picks the newest `>=0.1.0` and commits it to the cluster's repository, so a deployment is a commit and a rollback a `git revert`. `develop`, `main`, `latest` and commit tags are not versions and are ignored. Nothing is deployed until the maintainer scales the application up there.

## Consequences

- A release is a pull request `develop → main` that a person reviews and merges; everything after is automatic.
- The Conventional Commit title of every pull request decides the version: a `feat` that is really a `fix` over-bumps.
- A merge commit makes `main` a little noisier than a squash. It is the price of letting the analyzer see each change.
- If the release succeeds and the image build fails, the tag exists without an image; re-running the failed `image` job in the same run fixes it.

## Alternatives considered

- **A changelog and version bump committed by the pipeline** (`@semantic-release/git`): impossible with the no-direct-push ruleset, and a bypass for a bot is a hole not worth digging.
- **Squash merge of the release pull request**: hides the commits from the analyzer.
- **Release-please**: a fine tool, but a second pull request flow on top of ours.

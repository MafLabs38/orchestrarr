#!/usr/bin/env bash
# The test-bed repositories: put them back to their seed, so that a scenario of docs/CASE-GRID.md can be played again.
#
#   lab/sandbox.sh status [repo...]         what differs from the seed, the open pull requests
#   lab/sandbox.sh mark-seed <repo> <ref>   mark a commit as the seed of a repository (tag "lab-seed")
#   lab/sandbox.sh reset <repo...>|--all    close the open pull requests, delete the work branches, restore the seed tree
#   lab/sandbox.sh plant <scenario.json>    plant the faults of a scenario: a branch and a pull request for each (lab/scenarios/)
#   lab/sandbox.sh rebase <scenario.json>   what a dependency bot does when the base moved: the open pull requests of a scenario are made again on the base as it is now
#
# A development tool, not part of the product: it writes to the test-bed organisation only, with the token it is given.
# The repository rules stay what they are (pull request required, no force push): the seed tree is restored by a pull request
# that goes through the same checks as any other, never by rewriting history. The token comes from the environment
# (GH_TOKEN or GITHUB_TOKEN) and is never written to a file or to a command line; the organisation comes from LAB_ORG.
# Runs are serial: two at once close each other's pull requests.
#
# Needs git, gh, jq and npm. Optional: LAB_BASE_BRANCH (develop), LAB_WORKDIR (~/.cache/orchestrarr-lab), LAB_CHECK_TIMEOUT (600 s),
# LAB_REQUIRED_CHECK (build: the check that must have reported, and passed, before a restore is merged),
# LAB_BOT_AUTHORS (^app/(dependabot|renovate)$: the authors whose pull requests are left alone, a closed one is never opened again),
# LAB_BOT_BRANCHES (^(dependabot|renovate)/: the branches that are left alone with them).
set -euo pipefail
# command substitutions keep set -e (clone, wait_checks...); a function run on the left of || would lose it, so a repository is reset by a process of its own
shopt -s inherit_errexit

ORG="${LAB_ORG:?set LAB_ORG to the test-bed organisation}"
BASE="${LAB_BASE_BRANCH:-develop}"
WORK="${LAB_WORKDIR:-$HOME/.cache/orchestrarr-lab}"
TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
[ -n "$TOKEN" ] || { echo "no GH_TOKEN or GITHUB_TOKEN" >&2; exit 2; }
export GH_TOKEN="$TOKEN"
CHECK_TIMEOUT="${LAB_CHECK_TIMEOUT:-600}"
REQUIRED_CHECK="${LAB_REQUIRED_CHECK:-build}"
BOT_AUTHORS="${LAB_BOT_AUTHORS:-^app/(dependabot|renovate)\$}"
BOT_BRANCHES="${LAB_BOT_BRANCHES:-^(dependabot|renovate)/}"
NAME_RE='^[A-Za-z0-9][A-Za-z0-9._/-]*$'

# git reads the token from the environment through this helper: it never reaches a file or the argument list
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export GIT_ASKPASS="$HERE/git-askpass.sh" GIT_TERMINAL_PROMPT=0
export LAB_GIT_TOKEN="$TOKEN"

umask 077
mkdir -p "$WORK"

valid_name() { [[ "$1" =~ $NAME_RE ]] && [[ "$1" != *..* ]] || { echo "refused: '$1' is not a plain repository or branch name" >&2; return 1; }; }

# A clone of the repository, clean and up to date, with the seed tag.
clone() {
  local repo="$1" dir="$WORK/$1"
  valid_name "$repo"
  if [ ! -d "$dir/.git" ]; then
    git clone -q "https://github.com/${ORG}/${repo}.git" "$dir"
  fi
  git -C "$dir" remote set-url origin "https://github.com/${ORG}/${repo}.git"
  git -C "$dir" reset -q --hard
  git -C "$dir" clean -qfdx
  # The tags are taken as the forge has them (--force): a seed that was marked again moves its tag, and the tag is checked against the pinned seed before any use
  git -C "$dir" fetch -q --prune --tags --force origin
  echo "$dir"
}

# The identity of the lab's own commits: the account of its token, under the forge's no-reply address, so that the forge links each commit to the account that opens the pull request (a bot's new head is followed only when every
# commit of the pull request is its author's own, ADR 0055)
LAB_ACCOUNT="${LAB_ACCOUNT:-$(gh api user --jq '.login' 2>/dev/null)}"
LAB_EMAIL="${LAB_EMAIL:-$(gh api user --jq '"\(.id)+\(.login)@users.noreply.github.com"' 2>/dev/null)}"
lab_commit() { git -C "$1" -c user.name="${LAB_ACCOUNT:-lab}" -c user.email="${LAB_EMAIL:-lab@users.noreply.github.com}" commit -q -m "$2"; }

# The seed of a repository is pinned in lab/seeds.json (repository -> commit): the tag of the remote is only where the commit is fetched from.
# A moved tag, or a repository that is not pinned, is no seed: nothing is restored or planted there. A seed marked "red" has red pipelines as its fault (pipeline-scenarios): its restore is not held
# by its checks, and its pipelines are run on the base afterwards, so that the forge shows the default branch as the seed has it, as a real repository's would be.
SEEDS_FILE="${LAB_SEEDS_FILE:-$HERE/seeds.json}"
[ -r "$SEEDS_FILE" ] && jq -e 'type == "array"' "$SEEDS_FILE" >/dev/null 2>&1 || { echo "$SEEDS_FILE is missing or is not a list of {repository, seed}" >&2; exit 2; }
pinned_seed() { jq -r --arg r "$1" '.[] | select(.repository == $r) | .seed' "$SEEDS_FILE"; }
pinned_repositories() { jq -r '.[].repository' "$SEEDS_FILE"; }
pinned_red() { jq -r --arg r "$1" '.[] | select(.repository == $r) | (.red // false)' "$SEEDS_FILE"; }

# The pipelines of a red seed, run on the base: each workflow that can be started by hand
run_pipelines() {
  local repo="$1" id
  [ "$(pinned_red "$repo")" = true ] || return 0
  for id in $(gh workflow list -R "$ORG/$repo" --json id,state --jq '.[] | select(.state == "active") | .id'); do
    gh workflow run "$id" -R "$ORG/$repo" --ref "$BASE" >/dev/null 2>&1 || true
  done
  echo "$repo: its pipelines were run on $BASE, as its seed has them"
}
has_seed() {
  local dir="$1" repo="$2" pin tag
  pin=$(pinned_seed "$repo"); [ -n "$pin" ] || return 1
  tag=$(git -C "$dir" rev-parse -q --verify "refs/tags/lab-seed^{commit}") || return 1
  [ "$tag" = "$pin" ] || { echo "$repo: the tag lab-seed is $tag, the pinned seed is $pin: refused" >&2; return 1; }
}

# Same files as the seed? (trees, not history)
same_as_seed() {
  [ "$(git -C "$1" rev-parse "origin/$BASE^{tree}")" = "$(git -C "$1" rev-parse "lab-seed^{tree}")" ]
}

# Delete a remote branch; a branch that is already gone (deleted by the repository when its pull request merged, or never there) is not an error, anything else is.
delete_branch() {
  local dir="$1" branch="$2" out
  if ! out=$(git -C "$dir" push origin --delete "$branch" 2>&1); then
    # The reply says it in several ways (no such ref, a lock on a ref that was just deleted): what counts is whether the branch is still there
    if git -C "$dir" ls-remote --exit-code --heads origin "$branch" >/dev/null 2>&1; then
      echo "could not delete the branch $branch: $out" >&2
      return 1
    fi
  fi
}

cmd_status() {
  local repos=("$@"); [ ${#repos[@]} -gt 0 ] || mapfile -t repos < <(pinned_repositories)
  for repo in "${repos[@]}"; do
    local dir; dir=$(clone "$repo")
    if ! has_seed "$dir" "$repo"; then echo "$repo: no seed (pin one with mark-seed)"; continue; fi
    local state="at the seed"; same_as_seed "$dir" || state="DIFFERS from the seed"
    local prs; prs=$(gh pr list -R "$ORG/$repo" --state open --json number --jq 'length')
    echo "$repo: $BASE $state, $prs open pull request(s)"
  done
}

cmd_mark_seed() {
  local repo="$1" ref="$2" dir; dir=$(clone "$repo")
  git -C "$dir" tag -f lab-seed "$ref"
  git -C "$dir" push -q --force origin refs/tags/lab-seed
  local sha; sha=$(git -C "$dir" rev-parse "lab-seed^{commit}")
  local tmp; tmp=$(mktemp); jq --arg r "$repo" --arg s "$sha" 'if any(.[]; .repository == $r) then map(if .repository == $r then .seed = $s else . end) else . + [{repository: $r, seed: $s}] end' "$SEEDS_FILE" > "$tmp" && cat "$tmp" > "$SEEDS_FILE" && rm -f "$tmp"
  echo "$repo: seed is now ${sha:0:7}, pinned in $SEEDS_FILE (commit that file)"
}

close_pull_requests() {
  local repo="$1"
  # A closed bot pull request is never opened again by the bot: only the pull requests of the lab and of the agents are closed
  local numbers; mapfile -t numbers < <(gh pr list -R "$ORG/$repo" --state open --json number,author | jq -r --arg re "$BOT_AUTHORS" '.[] | select(.author.login | test($re) | not) | .number')
  for n in "${numbers[@]}"; do
    gh pr close "$n" -R "$ORG/$repo" -c "Lab reset: the repository goes back to its seed." >/dev/null
  done
}

delete_work_branches() {
  local dir="$1"
  for b in $(git -C "$dir" for-each-ref --format='%(refname:strip=3)' refs/remotes/origin | grep -vE "^(HEAD|$BASE|main)$|$BOT_BRANCHES" || true); do
    delete_branch "$dir" "$b"
  done
}

# pass, fail or wait. "No check yet" is a wait, never a pass: the required check must have reported, and every check must have passed.
checks_verdict() {
  local repo="$1" pr="$2"
  gh pr view "$pr" -R "$ORG/$repo" --json statusCheckRollup | jq -r --arg required "$REQUIRED_CHECK" '
    [.statusCheckRollup[] | {name: (.name // .context // ""), result: ((.conclusion // .state // "") | ascii_downcase)}] as $c
    | if any($c[]; .result | IN("failure", "error", "cancelled", "timed_out", "action_required")) then "fail"
      elif ($c | map(select(.name == $required)) | length) == 0 then "wait"
      elif all($c[]; .result | IN("success", "skipped", "neutral")) then "pass"
      else "wait" end' 2>/dev/null || echo wait
}

# Waits for the checks, and prints the head they passed on: the merge is pinned to it.
wait_checks() {
  local repo="$1" pr="$2" start; start=$(date +%s)
  while :; do
    local head; head=$(gh pr view "$pr" -R "$ORG/$repo" --json headRefOid --jq .headRefOid)
    case "$(checks_verdict "$repo" "$pr")" in
      pass) [ "$head" = "$(gh pr view "$pr" -R "$ORG/$repo" --json headRefOid --jq .headRefOid)" ] && { echo "$head"; return 0; } ;;
      fail) echo "$repo: the checks of #$pr failed" >&2; return 1 ;;
    esac
    [ $(( $(date +%s) - start )) -lt "$CHECK_TIMEOUT" ] || { echo "$repo: the checks of #$pr did not finish in ${CHECK_TIMEOUT}s" >&2; return 1; }
    sleep 10
  done
}

reset_one() {
  local repo="$1" dir; dir=$(clone "$repo")
  has_seed "$dir" "$repo" || { echo "$repo: no seed, nothing to restore" >&2; return 1; }
  close_pull_requests "$repo"
  delete_work_branches "$dir"
  git -C "$dir" fetch -q --prune --tags --force origin
  # The tag is checked again after every fetch: a tag moved on the forge meanwhile (any token that writes the repository's refs can) is never used as the seed
  has_seed "$dir" "$repo" || return 1
  if same_as_seed "$dir"; then echo "$repo: already at the seed"; run_pipelines "$repo"; return 0; fi

  local branch="lab/restore-$(date +%Y%m%d%H%M%S)"
  git -C "$dir" checkout -q -B "$branch" "origin/$BASE"
  git -C "$dir" rm -rq --ignore-unmatch . >/dev/null
  git -C "$dir" checkout -q lab-seed -- .
  git -C "$dir" add -A
  lab_commit "$dir" "chore(lab): restore the seed"
  git -C "$dir" push -q origin "$branch"
  local pr; pr=$(gh pr create -R "$ORG/$repo" --base "$BASE" --head "$branch" --title "chore(lab): restore the seed" --body "The test-bed repository goes back to its seed so that a scenario can be played again." | grep -oE '[0-9]+$')
  # The protected branches are updated by the administrators only (ADR 0057: so that an agent's push credential can never merge); the lab's account is one, and says so with --admin. On the test bed only
  # that rule lists the administrators as able to bypass it: the rule of the required checks lists no one, so the forge still waits for them. A test bed that lets the administrators bypass the checks too would merge
  # the seed without them (it is pinned, and checked against its tag above)
  # The forge can still list a required check as queued when the rollup already shows the earlier ones as passed: a merge it refuses for that is asked for again after the checks are waited for again
  local head attempt
  # A red seed is restored with its red pipelines: its checks fail by design, so the merge is pinned to the head that was pushed and does not wait for them
  if [ "$(pinned_red "$repo")" = true ]; then
    head=$(gh pr view "$pr" -R "$ORG/$repo" --json headRefOid --jq .headRefOid)
    gh pr merge "$pr" -R "$ORG/$repo" --squash --admin --match-head-commit "$head" >/dev/null || return 1
  fi
  for attempt in 1 2 3; do
    [ "$(pinned_red "$repo")" = true ] && break
    head=$(wait_checks "$repo" "$pr")
    if gh pr merge "$pr" -R "$ORG/$repo" --squash --admin --match-head-commit "$head" >/dev/null 2>"${TMPDIR:-/tmp}/lab-merge-$$.err"; then rm -f "${TMPDIR:-/tmp}/lab-merge-$$.err"; break; fi
    if [ "$attempt" = 3 ] || ! grep -qiE 'status check|queued|in progress|pending|base branch policy' "${TMPDIR:-/tmp}/lab-merge-$$.err"; then cat "${TMPDIR:-/tmp}/lab-merge-$$.err" >&2; rm -f "${TMPDIR:-/tmp}/lab-merge-$$.err"; return 1; fi
    sleep 15
  done
  # A repository may delete the branch of a merged pull request by itself: one that is already gone is not an error
  delete_branch "$dir" "$branch"
  git -C "$dir" fetch -q --prune --tags --force origin
  has_seed "$dir" "$repo" || return 1
  same_as_seed "$dir" || { echo "$repo: restored, but $BASE still differs from the seed" >&2; return 1; }
  echo "$repo: restored through #$pr"
  run_pipelines "$repo"
}

cmd_reset() {
  local repos=("$@")
  if [ "${repos[0]:-}" = "--all" ]; then mapfile -t repos < <(pinned_repositories); fi
  [ ${#repos[@]} -gt 0 ] || { echo "usage: reset <repo...> | --all" >&2; return 2; }
  local failed=0
  for repo in "${repos[@]}"; do "$0" reset-one "$repo" || failed=1; done
  return $failed
}

# A scenario lists faults to plant: {"plants": [{"repo", "branch", "title", "body", "kind": "npm-bump"|"patch", ...}]}
#   pip-bump: "package", "from", "version" (a pinned line of requirements.txt)
#   npm-bump: "package", "from", "version"  (the version the seed holds, checked; the lock file follows), "patches" (optional: files next to the scenario, applied with git apply)
#   patch: "patch" (a file next to the scenario, applied with git apply)
# What Dependabot does when the base already holds the version it proposed: it says so and closes its pull request
close_useless() {
  local repo="$1" branch="$2" package="$3" current="$4"
  gh pr close "$branch" -R "$ORG/$repo" --delete-branch --comment "Looks like $package is up-to-date now ($current on the base), so this is no longer needed." >/dev/null 2>&1 || true
  echo "$repo: closed $branch, $package is already $current on $BASE"
}

plant_one() {
  local scenario="$1" index="$2" dir item repo branch title body kind
  item=$(jq -c --argjson i "$index" '.plants[$i]' "$scenario")
  repo=$(jq -r .repo <<<"$item"); branch=$(jq -r .branch <<<"$item"); title=$(jq -r .title <<<"$item")
  body=$(jq -r '.body // "Planted by the lab."' <<<"$item"); kind=$(jq -r .kind <<<"$item")
  valid_name "$repo"; valid_name "$branch"
  case "$branch" in "$BASE"|main|HEAD) echo "refused: a scenario never plants on $branch" >&2; return 1 ;; esac
  dir=$(clone "$repo")
  has_seed "$dir" "$repo" || { echo "$repo: no seed, the lab does not plant here" >&2; return 1; }
  # A bot that rebases its pull request keeps the branch (and so the pull request): it is made again on the base as it is now
  [ -n "${REBASE:-}" ] || delete_branch "$dir" "$branch"
  git -C "$dir" checkout -q -B "$branch" "origin/$BASE"
  case "$kind" in
    npm-bump)
      local package from to current
      package=$(jq -r .package <<<"$item"); from=$(jq -r .from <<<"$item"); to=$(jq -r .version <<<"$item")
      # what npm is given is a package name and a version, never an option
      [[ "$package" =~ ^(@[a-z0-9][a-z0-9._-]*/)?[a-z0-9][a-z0-9._-]*$ ]] || { echo "refused: '$package' is not a package name" >&2; return 1; }
      [[ "$to" =~ ^[0-9]+\.[0-9]+\.[0-9]+([-+][0-9A-Za-z.-]+)?$ ]] || { echo "refused: '$to' is not a version" >&2; return 1; }
      current=$(jq -r --arg p "$package" '(.dependencies[$p] // .devDependencies[$p] // "")' "$dir/package.json")
      if [ -n "${REBASE:-}" ] && [ -n "$current" ] && [ "$(printf '%s\n%s\n' "$to" "${current#^}" | sort -V | tail -1)" = "${current#^}" ]; then close_useless "$repo" "$branch" "$package" "$current"; return 0; fi
      # a bot that makes its pull request again does not mind what the base holds now: it bumps to the version it was asked for (the first planting checks the seed)
      [ -n "${REBASE:-}" ] || [ "$current" = "$from" ] || { echo "$repo: $package is $current in the seed, the scenario says $from" >&2; return 1; }
      # npm reads the .npmrc of the repository, which may expand environment variables: it runs with an empty environment, none of the lab's credentials
      mkdir -p "$WORK/.npm-home"
      (cd "$dir" && env -i PATH="$PATH" HOME="$WORK/.npm-home" npm install --package-lock-only --save-exact --ignore-scripts --no-audit --no-fund "$package@$to" >/dev/null)
      # what else the author of the change touched with it (a file next to the scenario, applied with git apply)
      for patch in $(jq -r '.patches[]? // empty' <<<"$item"); do
        valid_name "$patch"
        # a bot that makes its pull request again on a base that already holds one of its hunks (the same base image, raised by another change) drops that hunk: git does the same
        if git -C "$dir" apply --reverse --check "$(dirname "$scenario")/$patch" 2>/dev/null; then continue; fi
        # made again on a base that was changed in the same place by someone else (an agent raised the same image): the bot drops what the base already holds rather than leave the pull request in conflict
        if [ -n "${REBASE:-}" ]; then git -C "$dir" apply "$(dirname "$scenario")/$patch" 2>/dev/null || echo "$repo: the base already holds the change that $patch carries: dropped"; continue; fi
        git -C "$dir" apply "$(dirname "$scenario")/$patch"
      done ;;
    pip-bump)
      # what a bot does to a pinned Python requirement: the line of the package in requirements.txt says the new version
      local package from to current
      package=$(jq -r .package <<<"$item"); from=$(jq -r .from <<<"$item"); to=$(jq -r .version <<<"$item")
      [[ "$package" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo "refused: '$package' is not a package name" >&2; return 1; }
      [[ "$to" =~ ^[0-9]+(\.[0-9]+)*$ ]] || { echo "refused: '$to' is not a version" >&2; return 1; }
      current=$(grep -iE "^${package}==" "$dir/requirements.txt" | head -1 | cut -d= -f3)
      [ -n "$current" ] || { echo "$repo: $package is not pinned in requirements.txt" >&2; return 1; }
      [ -n "${REBASE:-}" ] || [ "$current" = "$from" ] || { echo "$repo: $package is $current in the seed, the scenario says $from" >&2; return 1; }
      # a bot never goes down: a base that already holds this version or a newer one makes the pull request useless, and the bot closes it
      if [ -n "${REBASE:-}" ] && [ "$(printf '%s\n%s\n' "$to" "$current" | sort -V | tail -1)" = "$current" ]; then close_useless "$repo" "$branch" "$package" "$current"; return 0; fi
      sed -i -E "s/^(${package})==.*$/\1==${to}/I" "$dir/requirements.txt" ;;
    patch)
      local patch; patch=$(jq -r .patch <<<"$item"); valid_name "$patch"
      git -C "$dir" apply "$(dirname "$scenario")/$patch" ;;
    *) echo "unknown kind $kind" >&2; return 1 ;;
  esac
  git -C "$dir" add -A
  lab_commit "$dir" "$title"
  git -C "$dir" push -q -f origin "$branch"
  if [ -n "${REBASE:-}" ]; then
    # The forge updates the head of a pull request a few seconds after the push: the bot is done when the pull request shows the new commit
    local pushed seen=""; pushed=$(git -C "$dir" rev-parse HEAD)
    for _ in $(seq 1 30); do
      seen=$(gh pr view "$branch" -R "$ORG/$repo" --json headRefOid --jq .headRefOid 2>/dev/null || true)
      [ "$seen" = "$pushed" ] && break
      sleep 2
    done
    [ "$seen" = "$pushed" ] || { echo "$repo: the pull request of $branch still shows another commit after the push" >&2; return 1; }
    echo "$repo: rebased $branch on $BASE"
    return 0
  fi
  local pr; pr=$(gh pr create -R "$ORG/$repo" --base "$BASE" --head "$branch" --title "$title" --body "$body" | grep -oE '[0-9]+$')
  echo "$repo: planted #$pr ($title)"
}

cmd_plant() {
  local scenario n; scenario=$(realpath "$1"); n=$(jq '.plants | length' "$scenario")
  for i in $(seq 0 $((n - 1))); do plant_one "$scenario" "$i"; done
}

# What a dependency bot does when the base moved under its pull request: the same bump, made again on the base as it is now, on the same branch (the pull request stays open and its head moves). Only the pull requests that are still open and behind the base.
cmd_rebase() {
  local scenario n branch repo; scenario=$(realpath "$1"); n=$(jq '.plants | length' "$scenario")
  for i in $(seq 0 $((n - 1))); do
    repo=$(jq -r ".plants[$i].repo" "$scenario"); branch=$(jq -r ".plants[$i].branch" "$scenario")
    valid_name "$repo"; valid_name "$branch"
    # a bot rebases a pull request when its base moved under it, not at every look: only a pull request that is behind the base is made again
    if [ "$(gh pr view "$branch" -R "$ORG/$repo" --json state --jq .state 2>/dev/null)" = "OPEN" ] && [ "$(gh api "repos/$ORG/$repo/compare/$BASE...$branch" --jq .behind_by 2>/dev/null || echo 0)" -gt 0 ]; then REBASE=1 plant_one "$scenario" "$i"; fi
  done
}

case "${1:-}" in
  status) shift; cmd_status "$@" ;;
  mark-seed) shift; [ $# -eq 2 ] || { echo "usage: mark-seed <repo> <ref>" >&2; exit 2; }; cmd_mark_seed "$@" ;;
  reset) shift; cmd_reset "$@" ;;
  reset-one) shift; reset_one "$1" ;;  # one repository, in a process of its own so that set -e applies to every step
  rebase) shift; [ $# -eq 1 ] || { echo "usage: rebase <scenario.json>" >&2; exit 2; }; cmd_rebase "$1" ;;
  plant) shift; [ $# -eq 1 ] || { echo "usage: plant <scenario.json>" >&2; exit 2; }; cmd_plant "$1" ;;
  *) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

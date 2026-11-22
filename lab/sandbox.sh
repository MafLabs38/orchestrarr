#!/usr/bin/env bash
# The test-bed repositories: put them back to their seed, so that a scenario of docs/CASE-GRID.md can be played again.
#
#   lab/sandbox.sh status [repo...]         what differs from the seed, the open pull requests
#   lab/sandbox.sh mark-seed <repo> <ref>   mark a commit as the seed of a repository (tag "lab-seed")
#   lab/sandbox.sh reset <repo...>|--all    close the open pull requests, delete the work branches, restore the seed tree
#   lab/sandbox.sh plant <scenario.json>    plant the faults of a scenario: a branch and a pull request for each (lab/scenarios/)
#
# A development tool, not part of the product: it writes to the test-bed organisation only, with the token it is given.
# The repository rules stay what they are (pull request required, no force push): the seed tree is restored by a pull request
# that goes through the same checks as any other, never by rewriting history. The token comes from the environment
# (GH_TOKEN or GITHUB_TOKEN) and is never written to a file or to a command line; the organisation comes from LAB_ORG.
# Runs are serial: two at once close each other's pull requests.
#
# Needs git, gh, jq and npm. Optional: LAB_BASE_BRANCH (develop), LAB_WORKDIR (~/.cache/orchestrarr-lab), LAB_CHECK_TIMEOUT (600 s),
# LAB_REQUIRED_CHECK (build: the check that must have reported, and passed, before a restore is merged),
# LAB_BOT_AUTHORS (^app/(dependabot|renovate)$: the authors whose pull requests are left alone, a closed one is never opened again).
set -euo pipefail

ORG="${LAB_ORG:?set LAB_ORG to the test-bed organisation}"
BASE="${LAB_BASE_BRANCH:-develop}"
WORK="${LAB_WORKDIR:-$HOME/.cache/orchestrarr-lab}"
TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
[ -n "$TOKEN" ] || { echo "no GH_TOKEN or GITHUB_TOKEN" >&2; exit 2; }
export GH_TOKEN="$TOKEN"
CHECK_TIMEOUT="${LAB_CHECK_TIMEOUT:-600}"
REQUIRED_CHECK="${LAB_REQUIRED_CHECK:-build}"
BOT_AUTHORS="${LAB_BOT_AUTHORS:-^app/(dependabot|renovate)\$}"
NAME_RE='^[A-Za-z0-9][A-Za-z0-9._/-]*$'

# git reads the token from the environment through this helper: it never reaches a file or the argument list
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
export GIT_ASKPASS="$HERE/git-askpass.sh" GIT_TERMINAL_PROMPT=0
export LAB_GIT_TOKEN="$TOKEN"

umask 077
mkdir -p "$WORK"

all_repos() { gh repo list "$ORG" --limit 100 --json name --jq '.[].name'; }

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
  git -C "$dir" fetch -q --prune --tags --force origin
  echo "$dir"
}

has_seed() { git -C "$1" rev-parse -q --verify "refs/tags/lab-seed^{commit}" >/dev/null; }

# Same files as the seed? (trees, not history)
same_as_seed() {
  [ "$(git -C "$1" rev-parse "origin/$BASE^{tree}")" = "$(git -C "$1" rev-parse "lab-seed^{tree}")" ]
}

# Delete a remote branch; a branch that is already gone is not an error, anything else is.
delete_branch() {
  local dir="$1" branch="$2" out
  if ! out=$(git -C "$dir" push origin --delete "$branch" 2>&1); then
    case "$out" in
      *"remote ref does not exist"*) ;;
      *) echo "could not delete the branch $branch: $out" >&2; return 1 ;;
    esac
  fi
}

cmd_status() {
  local repos=("$@"); [ ${#repos[@]} -gt 0 ] || mapfile -t repos < <(all_repos)
  for repo in "${repos[@]}"; do
    local dir; dir=$(clone "$repo")
    if ! has_seed "$dir"; then echo "$repo: no seed (mark one with mark-seed)"; continue; fi
    local state="at the seed"; same_as_seed "$dir" || state="DIFFERS from the seed"
    local prs; prs=$(gh pr list -R "$ORG/$repo" --state open --json number --jq 'length')
    echo "$repo: $BASE $state, $prs open pull request(s)"
  done
}

cmd_mark_seed() {
  local repo="$1" ref="$2" dir; dir=$(clone "$repo")
  git -C "$dir" tag -f lab-seed "$ref"
  git -C "$dir" push -q --force origin refs/tags/lab-seed
  echo "$repo: seed is now $(git -C "$dir" rev-parse --short lab-seed)"
}

close_pull_requests() {
  local repo="$1"
  # A closed bot pull request is never opened again by the bot: only the pull requests of the lab and of the agents are closed
  for n in $(gh pr list -R "$ORG/$repo" --state open --json number,author | jq -r --arg re "$BOT_AUTHORS" '.[] | select(.author.login | test($re) | not) | .number'); do
    gh pr close "$n" -R "$ORG/$repo" -c "Lab reset: the repository goes back to its seed." >/dev/null
  done
}

delete_work_branches() {
  local dir="$1"
  for b in $(git -C "$dir" for-each-ref --format='%(refname:strip=3)' refs/remotes/origin | grep -vE "^(HEAD|$BASE|main)$|^dependabot/" || true); do
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
  has_seed "$dir" || { echo "$repo: no seed, nothing to restore" >&2; return 1; }
  close_pull_requests "$repo"
  delete_work_branches "$dir"
  git -C "$dir" fetch -q --prune --tags --force origin
  if same_as_seed "$dir"; then echo "$repo: already at the seed"; return 0; fi

  local branch="lab/restore-$(date +%Y%m%d%H%M%S)"
  git -C "$dir" checkout -q -B "$branch" "origin/$BASE"
  git -C "$dir" rm -rq --ignore-unmatch . >/dev/null
  git -C "$dir" checkout -q lab-seed -- .
  git -C "$dir" add -A
  git -C "$dir" -c user.name="lab" -c user.email="lab@users.noreply.github.com" commit -q -m "chore(lab): restore the seed"
  git -C "$dir" push -q origin "$branch"
  local pr; pr=$(gh pr create -R "$ORG/$repo" --base "$BASE" --head "$branch" --title "chore(lab): restore the seed" --body "The test-bed repository goes back to its seed so that a scenario can be played again." | grep -oE '[0-9]+$')
  local head; head=$(wait_checks "$repo" "$pr")
  gh pr merge "$pr" -R "$ORG/$repo" --squash --delete-branch --match-head-commit "$head" >/dev/null
  git -C "$dir" fetch -q --prune --tags --force origin
  same_as_seed "$dir" || { echo "$repo: restored, but $BASE still differs from the seed" >&2; return 1; }
  echo "$repo: restored through #$pr"
}

cmd_reset() {
  local repos=("$@")
  if [ "${repos[0]:-}" = "--all" ]; then mapfile -t repos < <(all_repos); fi
  [ ${#repos[@]} -gt 0 ] || { echo "usage: reset <repo...> | --all" >&2; return 2; }
  local failed=0
  for repo in "${repos[@]}"; do reset_one "$repo" || failed=1; done
  return $failed
}

# A scenario lists faults to plant: {"plants": [{"repo", "branch", "title", "body", "kind": "npm-bump"|"patch", ...}]}
#   npm-bump: "package", "from", "version"  (the version the seed holds, checked; the lock file follows)
#   patch: "patch" (a file next to the scenario, applied with git apply)
plant_one() {
  local scenario="$1" index="$2" dir item repo branch title body kind
  item=$(jq -c ".plants[$index]" "$scenario")
  repo=$(jq -r .repo <<<"$item"); branch=$(jq -r .branch <<<"$item"); title=$(jq -r .title <<<"$item")
  body=$(jq -r '.body // "Planted by the lab."' <<<"$item"); kind=$(jq -r .kind <<<"$item")
  valid_name "$repo"; valid_name "$branch"
  dir=$(clone "$repo")
  delete_branch "$dir" "$branch"
  git -C "$dir" checkout -q -B "$branch" "origin/$BASE"
  case "$kind" in
    npm-bump)
      local package from to current
      package=$(jq -r .package <<<"$item"); from=$(jq -r .from <<<"$item"); to=$(jq -r .version <<<"$item")
      current=$(jq -r --arg p "$package" '(.dependencies[$p] // .devDependencies[$p] // "")' "$dir/package.json")
      [ "$current" = "$from" ] || { echo "$repo: $package is $current in the seed, the scenario says $from" >&2; return 1; }
      (cd "$dir" && npm install --package-lock-only --save-exact --ignore-scripts --no-audit --no-fund "$package@$to" >/dev/null) ;;
    patch)
      local patch; patch=$(jq -r .patch <<<"$item"); valid_name "$patch"
      git -C "$dir" apply "$(dirname "$scenario")/$patch" ;;
    *) echo "unknown kind $kind" >&2; return 1 ;;
  esac
  git -C "$dir" add -A
  git -C "$dir" -c user.name="lab" -c user.email="lab@users.noreply.github.com" commit -q -m "$title"
  git -C "$dir" push -q -f origin "$branch"
  local pr; pr=$(gh pr create -R "$ORG/$repo" --base "$BASE" --head "$branch" --title "$title" --body "$body" | grep -oE '[0-9]+$')
  echo "$repo: planted #$pr ($title)"
}

cmd_plant() {
  local scenario="$1" n; n=$(jq '.plants | length' "$scenario")
  for i in $(seq 0 $((n - 1))); do plant_one "$scenario" "$i"; done
}

case "${1:-}" in
  status) shift; cmd_status "$@" ;;
  mark-seed) shift; [ $# -eq 2 ] || { echo "usage: mark-seed <repo> <ref>" >&2; exit 2; }; cmd_mark_seed "$@" ;;
  reset) shift; cmd_reset "$@" ;;
  plant) shift; [ $# -eq 1 ] || { echo "usage: plant <scenario.json>" >&2; exit 2; }; cmd_plant "$1" ;;
  *) sed -n '2,8p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

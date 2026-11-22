#!/usr/bin/env bash
# The test-bed repositories: put them back to their seed, so that a scenario of docs/CASE-GRID.md can be played again.
#
#   lab/sandbox.sh status [repo...]     what differs from the seed, the open pull requests
#   lab/sandbox.sh mark-seed <repo> <ref>   mark a commit as the seed of a repository (tag "lab-seed")
#   lab/sandbox.sh reset [repo...]      close the open pull requests, delete the work branches, restore the seed tree
#
# The repository rules stay what they are (pull request required, no force push): the seed tree is restored by a pull request
# that goes through the same checks as any other, never by rewriting history. Nothing here is a secret: the token comes from
# the environment (GH_TOKEN or GITHUB_TOKEN) and the organisation from LAB_ORG.
set -euo pipefail

ORG="${LAB_ORG:?set LAB_ORG to the test-bed organisation}"
BASE="${LAB_BASE_BRANCH:-develop}"
WORK="${LAB_WORKDIR:-$HOME/.cache/orchestrarr-lab}"
TOKEN="${GH_TOKEN:-${GITHUB_TOKEN:-}}"
[ -n "$TOKEN" ] || { echo "no GH_TOKEN or GITHUB_TOKEN" >&2; exit 2; }
export GH_TOKEN="$TOKEN"
CHECK_TIMEOUT="${LAB_CHECK_TIMEOUT:-600}"

mkdir -p "$WORK"

all_repos() { gh repo list "$ORG" --limit 100 --json name --jq '.[].name'; }

# A clone of the repository, up to date, with the seed tag.
clone() {
  local repo="$1" dir="$WORK/$1"
  if [ ! -d "$dir/.git" ]; then
    git clone -q "https://x-access-token:${TOKEN}@github.com/${ORG}/${repo}.git" "$dir"
  fi
  git -C "$dir" remote set-url origin "https://x-access-token:${TOKEN}@github.com/${ORG}/${repo}.git"
  git -C "$dir" fetch -q --prune --tags --force origin
  echo "$dir"
}

has_seed() { git -C "$1" rev-parse -q --verify "refs/tags/lab-seed^{commit}" >/dev/null; }

# Same files as the seed? (trees, not history)
same_as_seed() {
  [ "$(git -C "$1" rev-parse "origin/$BASE^{tree}")" = "$(git -C "$1" rev-parse "lab-seed^{tree}")" ]
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
  for n in $(gh pr list -R "$ORG/$repo" --state open --json number,author --jq '.[] | select(.author.login | test("dependabot") | not) | .number'); do
    gh pr close "$n" -R "$ORG/$repo" -c "Lab reset: the repository goes back to its seed." >/dev/null
  done
}

delete_work_branches() {
  local repo="$1" dir="$2"
  for b in $(git -C "$dir" for-each-ref --format='%(refname:strip=3)' refs/remotes/origin | grep -vE "^(HEAD|$BASE|main)$|^dependabot/" || true); do
    git -C "$dir" push -q origin --delete "$b" 2>/dev/null || true
  done
}

wait_checks() {
  local repo="$1" pr="$2" start; start=$(date +%s)
  while :; do
    # pass, fail or wait; "no check yet" is a wait, never a pass
    local verdict; verdict=$(gh pr view "$pr" -R "$ORG/$repo" --json statusCheckRollup --jq '
      [.statusCheckRollup[] | (.conclusion // "") | ascii_downcase] as $c
      | if ($c | length) == 0 then "wait"
        elif any($c[]; . == "failure" or . == "cancelled" or . == "timed_out" or . == "action_required") then "fail"
        elif all($c[]; . == "success" or . == "skipped" or . == "neutral") then "pass"
        else "wait" end' 2>/dev/null || echo wait)
    case "$verdict" in
      pass) return 0 ;;
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
  delete_work_branches "$repo" "$dir"
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
  wait_checks "$repo" "$pr"
  gh pr merge "$pr" -R "$ORG/$repo" --squash --delete-branch >/dev/null
  git -C "$dir" fetch -q --prune --tags --force origin
  same_as_seed "$dir" || { echo "$repo: restored, but $BASE still differs from the seed" >&2; return 1; }
  echo "$repo: restored through #$pr"
}

cmd_reset() {
  local repos=("$@"); [ ${#repos[@]} -gt 0 ] || mapfile -t repos < <(all_repos)
  local failed=0
  for repo in "${repos[@]}"; do reset_one "$repo" || failed=1; done
  return $failed
}

case "${1:-}" in
  status) shift; cmd_status "$@" ;;
  mark-seed) shift; [ $# -eq 2 ] || { echo "usage: mark-seed <repo> <ref>" >&2; exit 2; }; cmd_mark_seed "$@" ;;
  reset) shift; cmd_reset "$@" ;;
  *) sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac

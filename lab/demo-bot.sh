#!/usr/bin/env bash
# The dependency bot of the test bed, for a demo (ADR 0055): every half minute it makes again, on the base as it is now, the pull requests of a scenario that are open and behind it, as Dependabot does when a merge
# moved the base under them. The control plane asks a bot to rebase (a comment) and waits; this is what answers in the lab, for the pull requests that the lab planted (the real Dependabot pull requests answer by themselves).
#
#   lab/demo-bot.sh <scenario.json> [seconds]     runs until it is stopped (Ctrl-C, or kill)
#
# Same environment as sandbox.sh (LAB_ORG, GH_TOKEN). A development tool, never part of the product.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
scenario="${1:?usage: demo-bot.sh <scenario.json> [seconds]}"
every="${2:-30}"
[[ "$every" =~ ^[0-9]+$ ]] || { echo "the interval is a number of seconds" >&2; exit 2; }
trap 'echo "demo bot: stopped"; exit 0' INT TERM
echo "demo bot: watching $scenario every ${every}s"
while true; do
  "$here/sandbox.sh" rebase "$scenario" 2>&1 | grep -v '^remote:' || true
  sleep "$every" &
  wait $!
done

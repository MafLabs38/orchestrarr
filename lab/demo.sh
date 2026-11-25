#!/usr/bin/env bash
# The faults of a demo, planted together and left for a person to tick: every repository goes back to its seed, the pull requests of lab/scenarios/DEMO.json are planted, and the
# findings of the fake source are written (LAB_FINDINGS_FILE). Nothing is ticked and nothing is validated: that is what the person does, on the screens. Same environment as sandbox.sh.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
scenario="$here/scenarios/DEMO.json"
: "${LAB_FINDINGS_FILE:?set LAB_FINDINGS_FILE to the file of the control plane (Missions:SandboxFindingsFile)}"
# shellcheck disable=SC2046
"$here/sandbox.sh" reset $(node -e 'console.log(require(process.argv[1]).repos.join(" "))' "$scenario")
"$here/sandbox.sh" plant "$scenario"
node -e '
  const fs = require("fs"), scenario = require(process.argv[1]), org = process.env.LAB_ORG;
  fs.writeFileSync(process.argv[2], JSON.stringify(Object.fromEntries(Object.entries(scenario.findings).map(([repo, list]) => [`${org}/${repo}`, list]))));
' "$scenario" "$LAB_FINDINGS_FILE"
echo "demo planted: refresh the Decisions screen"

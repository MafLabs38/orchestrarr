#!/usr/bin/env node
// Plays one scenario of docs/CASE-GRID.md through the interface, the way the maintainer does, then checks the final state.
//   LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file> LAB_ORG=<test-bed org> GH_TOKEN=<token> \
//   NODE_PATH=<folder with playwright> node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
// Exit code 0 only when every expectation holds. Nothing of the sandbox (address, account) is kept in the repository.
// Whatever happens after the setup, the browser is closed and the sandbox is reset again (LAB_KEEP=1 keeps what the run made).
const fs = require('fs');
const path = require('path');
const { execFileSync, spawn } = require('child_process');
const { chromium } = require('playwright');

const scenarioPath = process.argv[2];
if (!scenarioPath) { console.error('usage: run-scenario.cjs <scenario.json>'); process.exit(2); }
const scenario = JSON.parse(fs.readFileSync(scenarioPath, 'utf8'));
const url = process.env.LAB_URL, user = process.env.LAB_USER, org = process.env.LAB_ORG;
const password = process.env.LAB_PASSWORD ?? (process.env.LAB_PASSWORD_FILE ? fs.readFileSync(process.env.LAB_PASSWORD_FILE, 'utf8').trim() : undefined);
if (!url || !user || !password || !org) { console.error('set LAB_URL, LAB_USER, LAB_PASSWORD or LAB_PASSWORD_FILE, and LAB_ORG'); process.exit(2); }
const maxSeconds = Number(process.env.LAB_MAX_SECONDS ?? 900);
const setup = !process.env.LAB_SKIP_SETUP;
const keep = Boolean(process.env.LAB_KEEP);
// The file where the fake source of advisories reads what is known about the dependencies (Missions:SandboxFindingsFile of the control plane of the test bed): a scenario with "findings" needs it
const findingsFile = process.env.LAB_FINDINGS_FILE;
if (scenario.findings && !findingsFile) { console.error('this scenario plants findings: set LAB_FINDINGS_FILE to the file of the control plane'); process.exit(2); }

// The words of the interface the runner drives, in one place
const UI = { signIn: /sign in/i, refresh: 'Refresh', validate: 'Validate the selection', started: /problems started|Nothing was started/, missions: 'Missions' };

const sandbox = (...args) => execFileSync(path.join(__dirname, '..', 'sandbox.sh'), args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] }).trim();
const repos = [...new Set([...(scenario.repos ?? []), ...(scenario.plants ?? []).map((p) => p.repo)])];
const runStart = Date.now() - 60000;
const failures = [];
// How long a row may stay unselectable: the runner is still putting its machine back after the previous run
const RUNNER_BACK_MS = 300000;
const expect = (ok, text) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${text}`); if (!ok) failures.push(text); };

// The state of a pull request on the forge, or NOT_FOUND when there is none (never an exception on that: the run goes on and reports).
// A pull request is named by its branch, or by its title when the lab did not choose it (a repair's number is only known along the way): "{0}" is the number of the first planted pull request
let plantedNumbers = [];
let followed = [];
function prState(want) {
  try {
    if (want.title) {
      const title = want.title.replace(/\{(\d+)\}/g, (_, index) => {
        if (plantedNumbers[Number(index)] === undefined) throw new Error(`the title of an expectation names the planted pull request {${index}}, which the scenario did not plant`);
        return String(plantedNumbers[Number(index)]);
      });
      const found = JSON.parse(execFileSync('gh', ['pr', 'list', '-R', `${org}/${want.repo}`, '--state', 'all', '--search', `"${title}" in:title`, '--json', 'state,title,createdAt', '--limit', '20'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
      // A title that the lab does not make unique (a fix has the same title at each run) is looked for among the pull requests opened since this run began
      return found.find((candidate) => candidate.title === title && (!want.thisRun || Date.parse(candidate.createdAt) >= runStart))?.state ?? 'NOT_FOUND';
    }
    return execFileSync('gh', ['pr', 'view', want.branch, '-R', `${org}/${want.repo}`, '--json', 'state', '--jq', '.state'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  } catch (error) {
    // only "there is no such pull request" is a state; a bad token or a network error is a failure of the run
    if (/no pull requests found/i.test(String(error.stderr ?? ''))) return 'NOT_FOUND';
    throw error;
  }
}

// The content of a file on the default branch of a repository, or null when there is none (the final state is read on the forge, not in the interface)
function fileOnForge(repo, filePath) {
  try {
    return execFileSync('gh', ['api', `repos/${org}/${repo}/contents/${filePath}`, '-H', 'Accept: application/vnd.github.raw+json'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] });
  } catch (error) {
    if (/Not Found|404/i.test(String(error.stderr ?? ''))) return null;
    throw error;
  }
}

// "1.2.10" is above "1.2.9": the numbers are compared one by one, a missing one counts as zero
// "greenPipelines": every workflow of the repository that ran on its base branch since the run began ended green on its latest run (they are waited for while they run)
function checkGreenPipelines(repo) {
  const base = scenario.base ?? 'develop';
  let latest = new Map();
  for (let attempt = 0; attempt < 60; attempt++) {
    const runs = JSON.parse(execFileSync('gh', ['run', 'list', '-R', `${org}/${repo}`, '--branch', base, '--limit', '50', '--json', 'workflowName,status,conclusion,createdAt,event'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
    latest = new Map();
    for (const run of runs.filter((candidate) => Date.parse(candidate.createdAt) >= runStart && candidate.event !== 'dynamic')) {
      if (!latest.has(run.workflowName)) latest.set(run.workflowName, run);
    }
    if (latest.size > 0 && [...latest.values()].every((run) => run.status === 'completed')) break;
    execFileSync('sleep', ['10']);
  }
  const red = [...latest.values()].filter((run) => run.status !== 'completed' || run.conclusion !== 'success').map((run) => `${run.workflowName} ${run.conclusion || run.status}`);
  expect(latest.size > 0 && red.length === 0, `the pipelines of ${repo} are green on its base (${latest.size} workflow(s)${red.length ? `; red: ${red.join(', ')}` : ''})`);
}

const versionAtLeast = (found, wanted) => {
  const numbers = (text) => String(text).replace(/^[^\d]*/, '').split(/[.\-+]/).map((part) => Number.parseInt(part, 10) || 0);
  const [a, b] = [numbers(found), numbers(wanted)];
  for (let index = 0; index < Math.max(a.length, b.length); index++) {
    if ((a[index] ?? 0) !== (b[index] ?? 0)) return (a[index] ?? 0) > (b[index] ?? 0);
  }
  return true;
};

// A check of the final content of the application: a dependency of package.json at least at a version, or a dependency that is gone; for any other file, a captured version or a pattern
function checkFile(want) {
  const content = fileOnForge(want.repo, want.path);
  if (content === null) { expect(false, `${want.repo}/${want.path} exists`); return; }
  // A file that is not JSON (a compose file, a Dockerfile): a number captured by a pattern is at least a version, or a pattern is present (matches) or absent (notMatches)
  if (want.capture) {
    const found = new RegExp(want.capture, 'm').exec(content)?.[1];
    expect(found !== undefined && versionAtLeast(found, want.atLeast), `${want.repo}/${want.path}: ${want.capture} is at least ${want.atLeast} (is ${found ?? 'absent'})`);
    return;
  }
  if (want.matches !== undefined || want.notMatches !== undefined) {
    if (want.matches !== undefined) expect(new RegExp(want.matches, 'm').test(content), `${want.repo}/${want.path} matches ${want.matches}`);
    if (want.notMatches !== undefined) expect(!new RegExp(want.notMatches, 'm').test(content), `${want.repo}/${want.path} does not match ${want.notMatches}`);
    return;
  }
  if (want.json === undefined) { expect(false, `${want.repo}/${want.path}: the check names none of capture, matches, notMatches or json`); return; }
  const value = want.json.split('.').reduce((node, key) => (node == null ? undefined : node[key]), JSON.parse(content));
  if (want.absent) expect(value === undefined, `${want.repo}/${want.path}: ${want.json} is gone (is ${value === undefined ? 'gone' : value})`);
  else expect(value !== undefined && versionAtLeast(value, want.atLeast), `${want.repo}/${want.path}: ${want.json} is at least ${want.atLeast} (is ${value ?? 'absent'})`);
}

async function signIn(page) {
  await page.goto(url);
  await page.getByLabel('Name', { exact: false }).first().fill(user);
  await page.getByLabel('Password', { exact: false }).first().fill(password);
  await page.getByRole('button', { name: UI.signIn }).click();
  await page.waitForLoadState('networkidle').catch(() => {});
}

// MissionState: Proposed, Mandated, Running, HandingOver, Accepted, Ended (the cause of an end is in "end": Done, Refused, Stopped...)
const apiList = async (page, route) => {
  const response = await page.context().request.get(`${url}${route}`);
  if (!response.ok()) throw new Error(`${route} answered ${response.status()}`);
  return response.json();
};
const missionsOf = async (page) => apiList(page, '/api/v1/missions?limit=100');
const problemRowsOf = async (page) => (await apiList(page, '/api/v1/problems')).rows;

// The table keeps what it read for a few minutes: ask for fresh readings until it shows the pull requests the lab has just planted
async function waitForPlantedRows(page, planted) {
  const shownNow = async () => new Set((await problemRowsOf(page)).map((r) => r.pullRequest).filter((n) => n != null));
  // The forge is read again at most every five minutes (GitHubOptions.CacheSeconds): the closed pull request of a run that has just ended can stay on the table for that long
  for (let attempt = 0; attempt < 60; attempt++) {
    const [reading] = await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/v1/problems') && r.url().includes('refresh=true'), { timeout: 60000 }),
      page.getByRole('button', { name: UI.refresh }).click(),
    ]);
    if (!reading.ok()) return false;
    const shown = await shownNow();
    // The table keeps what it read for a few minutes: right after a reset it may still show the closed pull request of the run before, next to the new one. What is ticked must be on one row.
    const rows = await problemRowsOf(page);
    const unique = (scenario.tick ?? []).every((text) => rows.filter((row) => String(row.description ?? '').includes(text)).length === 1);
    if (planted.every((n) => shown.has(n)) && unique) { await page.reload(); return true; }
    await page.waitForTimeout(5000);
  }
  return false;
}

// The table shows the commit of every pull request that is still open as the forge has it now (a bot made some again: the table is read again until it says so)
async function waitForFreshHeads(page) {
  for (let attempt = 0; attempt < 12; attempt++) {
    const open = (scenario.plants ?? []).map((p) => {
      try { return JSON.parse(execFileSync('gh', ['pr', 'view', p.branch, '-R', `${org}/${p.repo}`, '--json', 'number,state,headRefOid'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] })); } catch { return null; }
    }).filter((pr) => pr && pr.state === 'OPEN');
    await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/v1/problems') && r.url().includes('refresh=true'), { timeout: 60000 }),
      page.getByRole('button', { name: UI.refresh }).click(),
    ]);
    const rows = await problemRowsOf(page);
    if (open.every((pr) => rows.some((row) => row.pullRequest === pr.number && row.headSha === pr.headRefOid))) { await page.reload(); return true; }
    await page.waitForTimeout(5000);
  }
  return false;
}

async function tickAndValidate(page, ticks) {
  let selectionOk = true;
  for (const pattern of ticks ?? []) {
    // the table fills in after the page has loaded
    await page.waitForFunction((text) => document.body.innerText.includes(text), pattern, { timeout: 60000 }).catch(() => {});
    // Only the table of the decisions to take: the decisions taken below it list what earlier runs did
    const toTake = page.getByRole('table', { name: 'Decisions to take' });
    const row = toTake.locator('tbody tr', { hasText: pattern }).first();
    const matching = await toTake.locator('tbody tr', { hasText: pattern }).count();
    expect(matching === 1, `the table lists exactly one problem with "${pattern}" (lists ${matching})`);
    // the runner may still be putting its machine back after the previous run: the row is not selectable until it asks for work again
    if (matching === 1) await row.getByRole('checkbox').first().check({ timeout: RUNNER_BACK_MS }); else selectionOk = false;
  }
  // a wrong or partial selection is never validated: it would start a real chain that the scenario cannot judge
  if (!selectionOk) return false;
  // What the control plane answered to the validation, word for word: a refusal says why
  let validation = '';
  page.on('response', async (r) => { if (r.url().includes('/api/v1/plan/validate')) validation = `${r.status()} ${(await r.text().catch(() => '')).slice(0, 700)}`; });
  await page.getByRole('button', { name: UI.validate }).click();
  const started = page.getByText(UI.started).first();
  await started.waitFor({ timeout: 150000 }).catch(() => {});
  const answer = (await started.innerText().catch(() => '')) || '(no answer)';
  console.log('answer:', answer);
  // The reasons are in the block that holds the answer
  if (/Nothing was started/.test(answer)) console.log('answered:', validation);
  if (/Nothing was started/.test(answer)) console.log('why:', ((await started.locator('xpath=..').innerText().catch(() => '')) || '').replace(/\s+/g, ' ').slice(0, 600));
  expect(/started/.test(answer) && !/Nothing was started/.test(answer), 'the validation started the problem');
  return true;
}

// Follows the missions this validation created until the expected pull requests are in their final state and released, or one ends
// "lastWorld": the world where the chain ends when the application is not deployed by the product (a compose stack is merged, not deployed): Git; the release world by default
// "endsWithoutDone": how many missions may end without being done in this round (a problem that the control plane ends at once, such as a pull request that conflicts with its base)
// "ends": "escalated": the round ends well when its problem is escalated to a person (the agent cannot solve it): nothing goes on, and what it merged is what the round expects (nothing, by default)
async function followMissions(page, known, round = scenario) {
  await page.getByRole('link', { name: UI.missions }).click();
  const begin = Date.now();
  const escalates = round.ends === 'escalated';
  const wanted = round.expect?.pullRequests ?? [];
  const mayEnd = round.endsWithoutDone ?? 0;
  const lastWorld = round.lastWorld ?? 'Release';
  const reached = () => wanted.every((w) => prState(w) === w.state);
  let last = '', outcome = 'timeout', mine = [];
  while ((Date.now() - begin) / 1000 < maxSeconds) {
    mine = (await missionsOf(page)).filter((m) => !known.has(m.id));
    const labels = mine.map((m) => `${m.world} ${m.state}${m.end ? ` (${m.end})` : ''}`).join(' | ');
    if (labels !== last) { console.log(`[${Math.round((Date.now() - begin) / 1000)}s] ${labels}`); last = labels; }
    if (escalates) {
      const ours = (await apiList(page, '/api/v1/flow')).filter((problem) => Date.parse(problem.startedAt) >= runStart);
      if (ours.length > 0 && ours.every((problem) => problem.state === 'escalated') && mine.every((m) => m.state === 'Ended' || m.state === 'Accepted')) { outcome = 'escalated'; break; }
      if (ours.some((problem) => problem.state === 'closed')) { outcome = 'closed although it should escalate'; break; }
      await page.waitForTimeout(5000);
      continue;
    }
    if (mine.filter((m) => m.state === 'Ended' && m.end !== 'Done').length > mayEnd) { outcome = 'ended badly'; break; }
    // done when what the round expects is on the forge and no mission is still going: each is ended or accepted (a problem that waits in line has no mission yet, but what it merges is expected)
    const finished = mine.every((m) => m.state === 'Ended' || m.state === 'Accepted');
    if (mine.some((m) => m.world === lastWorld && m.state === 'Accepted') && reached() && finished) { outcome = 'released'; break; }
    await page.waitForTimeout(5000);
  }
  followed = mine;
  if (escalates) {
    expect(outcome === 'escalated', `the problem was escalated to a person, nothing going on (${outcome}; last: ${last || 'nothing'})`);
    return;
  }
  expect(outcome === 'released', `the chain went from staging to the ${lastWorld.toLowerCase()} (${outcome}; last: ${last || 'nothing'})`);
  if (outcome !== 'released') for (const m of mine) console.log(`${m.world} ${m.state}: end=${m.end ?? ''} ${m.endReason ?? ''}`);
}

(async () => {
  let browser;
  let bot;
  try {
    // 1 and 2. the sandbox goes back to its seed, and the faults of the scenario are planted
    let planted = [];
    if (setup) {
      if (scenario.findings) fs.writeFileSync(findingsFile, JSON.stringify(Object.fromEntries(Object.entries(scenario.findings).map(([repo, list]) => [`${org}/${repo}`, list]))));
      console.log(sandbox('reset', ...repos));
      const output = sandbox('plant', scenarioPath);
      console.log(output);
      planted = [...output.matchAll(/planted #(\d+)/g)].map((m) => Number(m[1]));
      plantedNumbers = planted;
    }
    else if (scenario.findings) fs.writeFileSync(findingsFile, JSON.stringify(Object.fromEntries(Object.entries(scenario.findings).map(([repo, list]) => [`${org}/${repo}`, list]))));

    browser = await chromium.launch({ headless: true });
    const page = await (await browser.newContext({ viewport: { width: 1700, height: 1000 } })).newPage();
    const problems = [];
    page.on('pageerror', (e) => problems.push('pageerror: ' + e.message));
    page.on('console', (m) => { if (m.type() === 'error' && !m.text().startsWith('Failed to load resource')) problems.push('console: ' + m.text().slice(0, 160)); });
    page.on('response', (r) => { if (r.status() >= 400 && r.url().includes('/api/') && !(r.status() === 401 && r.url().includes('/auth/'))) problems.push(`${r.status()} ${r.url()}`); });

    await signIn(page);
    // The history of earlier runs stays in the system: what counts is what appears after this validation
    const known = new Set((await missionsOf(page)).map((m) => m.id));

    // 3. the planted problems are shown, ticked and validated
    if (planted.length > 0) {
      const shown = await waitForPlantedRows(page, planted);
      expect(shown, `the table shows the planted pull requests (${planted.map((n) => '#' + n).join(', ')})`);
    } else {
      // Nothing planted on the forge (a problem that has no pull request): the table is read afresh once, so that it shows the current state
      await Promise.all([page.waitForResponse((r) => r.url().includes('/api/v1/problems') && r.url().includes('refresh=true'), { timeout: 60000 }), page.getByRole('button', { name: UI.refresh }).click()]);
      await page.reload();
    }
    // "bot": the lab's dependency bot runs while the missions go on, every so many seconds, and answers the control plane's requests to rebase as Dependabot does (ADR 0055)
    if (scenario.bot) bot = spawn(path.join(__dirname, '..', 'demo-bot.sh'), [scenarioPath, String(scenario.bot)], { stdio: ['ignore', 'ignore', 'inherit'], detached: true });
    // The bot never outlives the run: an interrupted run (Ctrl-C, a kill) stops it too
    if (bot) for (const signal of ['SIGINT', 'SIGTERM', 'exit']) process.once(signal, () => { try { process.kill(-bot.pid, 'SIGTERM'); } catch { /* already gone */ } if (signal !== 'exit') process.exit(130); });
    if (await tickAndValidate(page, scenario.tick)) {
      // 4. the missions go on, and the final state is read from the forge, not from the interface
      await followMissions(page, known);
    }
    // A second round: a bot makes again the pull requests that conflicted with the base, and the maintainer ticks them again
    if (scenario.then) {
      for (const want of scenario.expect?.pullRequests ?? []) {
        const state = prState(want);
        expect(state === want.state, `after the first round: ${want.repo} ${want.title ?? want.branch} is ${want.state} (is ${state})`);
      }
      console.log(sandbox('rebase', scenarioPath));
      const knownNow = new Set((await missionsOf(page)).map((m) => m.id));
      await page.getByRole('link', { name: 'Decisions' }).click().catch(() => {});
      expect(await waitForFreshHeads(page), 'the table shows the commits that the bot made');
      if (await tickAndValidate(page, scenario.then.tick)) await followMissions(page, knownNow, scenario.then);
    }
    for (const want of (scenario.then ?? scenario).expect?.pullRequests ?? []) {
      const state = prState(want);
      expect(state === want.state, `${want.repo} ${want.title ?? want.branch} is ${want.state} (is ${state})`);
    }
    for (const want of scenario.expect?.files ?? []) checkFile(want);
    for (const repo of scenario.expect?.greenPipelines ?? []) checkGreenPipelines(repo);
    // What the missions of this run said along the way (the lead's marks, the controller's decisions), as the Missions screen shows it
    if ((scenario.expect?.progress ?? []).length > 0) {
      const lines = [];
      for (const mission of followed) lines.push(...((await apiList(page, `/api/v1/missions/${mission.id}`)).progress ?? []));
      for (const text of scenario.expect.progress) expect(lines.some((line) => line.includes(text)), `the missions said "${text}"`);
    }
    expect(problems.length === 0, problems.length ? 'page errors: ' + problems.join(' | ') : 'no page error, no failing API call');
  } catch (error) {
    expect(false, `the run failed: ${String(error.message).split('\n')[0]}`);
  } finally {
    if (bot) try { process.kill(-bot.pid, 'SIGTERM'); } catch { /* already gone */ }
    if (browser) await browser.close().catch(() => {});
    // the advisories go away with the scenario
    if (scenario.findings && !keep) fs.writeFileSync(findingsFile, '{}');
    // 5. the sandbox goes back to its seed, whatever happened
    if (setup && !keep) {
      try { console.log(sandbox('reset', ...repos)); } catch (error) { expect(false, `the final reset failed: ${String(error.message).split('\n')[0]}`); }
    }
  }
  console.log(failures.length ? `\n${scenario.case}: NOT ticked (${failures.length} failure(s))` : `\n${scenario.case}: ticked`);
  process.exit(failures.length ? 1 : 0);
})().catch((e) => { console.error('failed:', String(e.message).split('\n')[0]); process.exit(1); });

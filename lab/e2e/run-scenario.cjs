#!/usr/bin/env node
// Plays one scenario of docs/CASE-GRID.md through the interface, the way the maintainer does, then checks the final state.
//   LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file> LAB_ORG=<test-bed org> GH_TOKEN=<token> \
//   NODE_PATH=<folder with playwright> node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
// Exit code 0 only when every expectation holds. Nothing of the sandbox (address, account) is kept in the repository.
// Whatever happens after the setup, the browser is closed and the sandbox is reset again (LAB_KEEP=1 keeps what the run made).
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
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

// The words of the interface the runner drives, in one place
const UI = { signIn: /sign in/i, refresh: 'Refresh', validate: 'Validate the selection', started: /problems started|Nothing was started/, missions: 'Missions' };

const sandbox = (...args) => execFileSync(path.join(__dirname, '..', 'sandbox.sh'), args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] }).trim();
const repos = [...new Set((scenario.plants ?? []).map((p) => p.repo))];
const failures = [];
const expect = (ok, text) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${text}`); if (!ok) failures.push(text); };

// The state of a pull request on the forge, or NOT_FOUND when there is none (never an exception: the run goes on and reports)
function prState(repo, branch) {
  try {
    return execFileSync('gh', ['pr', 'view', branch, '-R', `${org}/${repo}`, '--json', 'state', '--jq', '.state'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim();
  } catch {
    return 'NOT_FOUND';
  }
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
  for (let attempt = 0; attempt < 12; attempt++) {
    const [reading] = await Promise.all([
      page.waitForResponse((r) => r.url().includes('/api/v1/problems') && r.url().includes('refresh=true'), { timeout: 60000 }),
      page.getByRole('button', { name: UI.refresh }).click(),
    ]);
    if (!reading.ok()) return false;
    const shown = await shownNow();
    if (planted.every((n) => shown.has(n))) { await page.reload(); return true; }
    await page.waitForTimeout(5000);
  }
  return false;
}

async function tickAndValidate(page) {
  for (const pattern of scenario.tick ?? []) {
    const row = page.locator('tbody tr', { hasText: pattern }).first();
    const listed = (await row.count()) > 0;
    expect(listed, `the table lists a problem with "${pattern}"`);
    if (listed) await row.getByRole('checkbox').first().check();
  }
  await page.getByRole('button', { name: UI.validate }).click();
  const started = page.getByText(UI.started).first();
  await started.waitFor({ timeout: 30000 }).catch(() => {});
  const answer = (await started.innerText().catch(() => '')) || '(no answer)';
  console.log('answer:', answer);
  expect(/started/.test(answer) && !/Nothing was started/.test(answer), 'the validation started the problem');
}

// Follows the missions this validation created until the expected pull requests are in their final state and released, or one ends
async function followMissions(page, known) {
  await page.getByRole('link', { name: UI.missions }).click();
  const begin = Date.now();
  const wanted = scenario.expect?.pullRequests ?? [];
  const reached = () => wanted.every((w) => prState(w.repo, w.branch) === w.state);
  let last = '', outcome = 'timeout', mine = [];
  while ((Date.now() - begin) / 1000 < maxSeconds) {
    mine = (await missionsOf(page)).filter((m) => !known.has(m.id));
    const labels = mine.map((m) => `${m.world} ${m.state}${m.end ? ` (${m.end})` : ''}`).join(' | ');
    if (labels !== last) { console.log(`[${Math.round((Date.now() - begin) / 1000)}s] ${labels}`); last = labels; }
    if (mine.some((m) => m.state === 'Ended' && m.end !== 'Done')) { outcome = 'ended badly'; break; }
    if (mine.some((m) => m.world === 'Release' && m.state === 'Accepted') && reached()) { outcome = 'released'; break; }
    await page.waitForTimeout(5000);
  }
  expect(outcome === 'released', `the chain went from staging to the release (${outcome}; last: ${last || 'nothing'})`);
  if (outcome !== 'released') for (const m of mine) console.log(`${m.world} ${m.state}: end=${m.end ?? ''} ${m.endReason ?? ''}`);
}

(async () => {
  // 1 and 2. the sandbox goes back to its seed, and the faults of the scenario are planted
  let planted = [];
  if (setup) {
    console.log(sandbox('reset', ...repos));
    const output = sandbox('plant', scenarioPath);
    console.log(output);
    planted = [...output.matchAll(/planted #(\d+)/g)].map((m) => Number(m[1]));
  }

  let browser;
  try {
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
    }
    await tickAndValidate(page);

    // 4. the missions go on, and the final state is read from the forge, not from the interface
    await followMissions(page, known);
    for (const want of scenario.expect?.pullRequests ?? []) {
      const state = prState(want.repo, want.branch);
      expect(state === want.state, `${want.repo} ${want.branch} is ${want.state} (is ${state})`);
    }
    expect(problems.length === 0, problems.length ? 'page errors: ' + problems.join(' | ') : 'no page error, no failing API call');
  } catch (error) {
    expect(false, `the run failed: ${String(error.message).split('\n')[0]}`);
  } finally {
    if (browser) await browser.close().catch(() => {});
    // 5. the sandbox goes back to its seed, whatever happened
    if (setup && !keep) {
      try { console.log(sandbox('reset', ...repos)); } catch (error) { expect(false, `the final reset failed: ${String(error.message).split('\n')[0]}`); }
    }
  }
  console.log(failures.length ? `\n${scenario.case}: NOT ticked (${failures.length} failure(s))` : `\n${scenario.case}: ticked`);
  process.exit(failures.length ? 1 : 0);
})().catch((e) => { console.error('failed:', String(e.message).split('\n')[0]); process.exit(1); });

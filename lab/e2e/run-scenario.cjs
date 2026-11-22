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

// The state of a pull request on the forge, or NOT_FOUND when there is none (never an exception on that: the run goes on and reports).
// A pull request is named by its branch, or by its title when the lab did not choose it (a repair's number is only known along the way): "{0}" is the number of the first planted pull request
let plantedNumbers = [];
function prState(want) {
  try {
    if (want.title) {
      const title = want.title.replace(/\{(\d+)\}/g, (_, index) => String(plantedNumbers[Number(index)] ?? ''));
      const found = JSON.parse(execFileSync('gh', ['pr', 'list', '-R', `${org}/${want.repo}`, '--state', 'all', '--search', `"${title}" in:title`, '--json', 'state,title', '--limit', '20'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }));
      return found.find((candidate) => candidate.title === title)?.state ?? 'NOT_FOUND';
    }
    return execFileSync('gh', ['pr', 'view', want.branch, '-R', `${org}/${want.repo}`, '--json', 'state', '--jq', '.state'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
  } catch (error) {
    // only "there is no such pull request" is a state; a bad token or a network error is a failure of the run
    if (/no pull requests found/i.test(String(error.stderr ?? ''))) return 'NOT_FOUND';
    throw error;
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
  let selectionOk = true;
  for (const pattern of scenario.tick ?? []) {
    // the table fills in after the page has loaded
    await page.waitForFunction((text) => document.body.innerText.includes(text), pattern, { timeout: 60000 }).catch(() => {});
    const row = page.locator('tbody tr', { hasText: pattern }).first();
    const matching = await page.locator('tbody tr', { hasText: pattern }).count();
    expect(matching === 1, `the table lists exactly one problem with "${pattern}" (lists ${matching})`);
    if (matching === 1) await row.getByRole('checkbox').first().check(); else selectionOk = false;
  }
  // a wrong or partial selection is never validated: it would start a real chain that the scenario cannot judge
  if (!selectionOk) return false;
  await page.getByRole('button', { name: UI.validate }).click();
  const started = page.getByText(UI.started).first();
  await started.waitFor({ timeout: 30000 }).catch(() => {});
  const answer = (await started.innerText().catch(() => '')) || '(no answer)';
  console.log('answer:', answer);
  expect(/started/.test(answer) && !/Nothing was started/.test(answer), 'the validation started the problem');
  return true;
}

// Follows the missions this validation created until the expected pull requests are in their final state and released, or one ends
async function followMissions(page, known) {
  await page.getByRole('link', { name: UI.missions }).click();
  const begin = Date.now();
  const wanted = scenario.expect?.pullRequests ?? [];
  const reached = () => wanted.every((w) => prState(w) === w.state);
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
  let browser;
  try {
    // 1 and 2. the sandbox goes back to its seed, and the faults of the scenario are planted
    let planted = [];
    if (setup) {
      console.log(sandbox('reset', ...repos));
      const output = sandbox('plant', scenarioPath);
      console.log(output);
      planted = [...output.matchAll(/planted #(\d+)/g)].map((m) => Number(m[1]));
      plantedNumbers = planted;
    }

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
    if (await tickAndValidate(page)) {
      // 4. the missions go on, and the final state is read from the forge, not from the interface
      await followMissions(page, known);
    }
    for (const want of scenario.expect?.pullRequests ?? []) {
      const state = prState(want);
      expect(state === want.state, `${want.repo} ${want.title ?? want.branch} is ${want.state} (is ${state})`);
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

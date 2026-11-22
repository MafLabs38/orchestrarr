#!/usr/bin/env node
// Plays one scenario of docs/CASE-GRID.md through the interface, the way the maintainer does, then checks the final state.
//   LAB_URL=<control plane> LAB_USER=<account> LAB_PASSWORD_FILE=<file> LAB_ORG=<test-bed org> GH_TOKEN=<token> \
//   NODE_PATH=<folder with playwright> node lab/e2e/run-scenario.cjs lab/scenarios/CASE-01.json
// Exit code 0 only when every expectation holds. Nothing of the sandbox (address, account) is kept in the repository.
const fs = require('fs');
const { execFileSync } = require('child_process');
const { chromium } = require('playwright');

const scenarioPath = process.argv[2];
if (!scenarioPath) { console.error('usage: run-scenario.cjs <scenario.json>'); process.exit(2); }
const scenario = JSON.parse(fs.readFileSync(scenarioPath, 'utf8'));
const url = process.env.LAB_URL, user = process.env.LAB_USER, org = process.env.LAB_ORG;
const password = process.env.LAB_PASSWORD ?? (process.env.LAB_PASSWORD_FILE ? fs.readFileSync(process.env.LAB_PASSWORD_FILE, 'utf8').trim() : undefined);
if (!url || !user || !password || !org) { console.error('set LAB_URL, LAB_USER, LAB_PASSWORD or LAB_PASSWORD_FILE, and LAB_ORG'); process.exit(2); }
const maxSeconds = Number(process.env.LAB_MAX_SECONDS ?? 900);

const lab = require('path').join(__dirname, '..', 'sandbox.sh');
const sandbox = (...args) => execFileSync(lab, args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'inherit'] });
const repos = [...new Set((scenario.plants ?? []).map((p) => p.repo))];

const failures = [];
const expect = (ok, text) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${text}`); if (!ok) failures.push(text); };
const gh = (...args) => execFileSync('gh', args, { encoding: 'utf8' }).trim();

let plantedNumbers = [];
(async () => {
  // 1 and 2. the sandbox goes back to its seed, and the faults of the scenario are planted (LAB_SKIP_SETUP=1 plays on what is there)
  if (!process.env.LAB_SKIP_SETUP) {
    console.log(sandbox('reset', ...repos).trim());
    const planted = sandbox('plant', scenarioPath).trim();
    console.log(planted);
    plantedNumbers = [...planted.matchAll(/planted #(\d+)/g)].map((m) => Number(m[1]));
  }
  const browser = await chromium.launch({ headless: true });
  const page = await (await browser.newContext({ viewport: { width: 1700, height: 1000 } })).newPage();
  const problems = [];
  page.on('pageerror', (e) => problems.push('pageerror: ' + e.message));
  page.on('console', (m) => { if (m.type() === 'error' && !/401/.test(m.text())) problems.push('console: ' + m.text().slice(0, 160)); });
  page.on('response', (r) => { if (r.status() >= 400 && r.status() !== 401 && r.url().includes('/api/')) problems.push(`${r.status()} ${r.url()}`); });

  await page.goto(url);
  await page.getByLabel(/name/i).fill(user);
  await page.getByLabel(/password/i).fill(password);
  await page.getByRole('button', { name: /sign in/i }).click();

  // The history of earlier runs stays in the system: what counts is what appears after this validation
  const missionsOf = async () => {
    const r = await page.context().request.get(`${url}/api/v1/missions?limit=100`);
    const body = await r.json();
    return Array.isArray(body) ? body : (body.items ?? body.missions ?? []);
  };
  await page.waitForLoadState('networkidle').catch(() => {});
  const known = new Set((await missionsOf()).map((m) => m.id));

  // The table keeps what it read for a few minutes: ask for fresh readings until it shows the pull requests the lab has just planted
  // (and none of the earlier ones, closed since, that would still be offered)
  const problemsOf = async () => {
    const r = await page.context().request.get(`${url}/api/v1/problems`);
    const body = await r.json();
    return Array.isArray(body) ? body : (body.rows ?? body.items ?? body.problems ?? []);
  };
  if (plantedNumbers.length > 0) {
    for (let attempt = 0; attempt < 12; attempt++) {
      await page.getByRole('button', { name: 'Refresh' }).click();
      await page.getByRole('button', { name: 'Refresh' }).isEnabled({ timeout: 60000 }).catch(() => {});
      await page.waitForFunction(() => !document.querySelector('button[disabled]'), null, { timeout: 60000 }).catch(() => {});
      const shown = new Set((await problemsOf()).map((r) => r.pullRequest).filter((n) => n != null));
      if (plantedNumbers.every((n) => shown.has(n))) break;
      await page.waitForTimeout(5000);
    }
    const shown = new Set((await problemsOf()).map((r) => r.pullRequest).filter((n) => n != null));
    expect(plantedNumbers.every((n) => shown.has(n)), `the table shows the planted pull requests (${plantedNumbers.map((n) => '#' + n).join(', ')})`);
    await page.reload();
  }

  // 3. the problem is listed, and ticked
  for (const pattern of scenario.tick ?? []) {
    await page.waitForFunction((t) => document.body.innerText.includes(t), pattern, { timeout: 120000 }).catch(() => {});
    const row = page.locator('tbody tr', { hasText: pattern }).first();
    const listed = (await row.count()) > 0;
    expect(listed, `the table lists a problem with "${pattern}"`);
    if (!listed) continue;
    await row.getByRole('checkbox').first().check();
  }
  await page.getByRole('button', { name: 'Validate the selection' }).click();
  const started = page.getByText(/problems started|Nothing was started/).first();
  await started.waitFor({ timeout: 30000 }).catch(() => {});
  const alert = (await started.innerText().catch(() => '')) || '(no answer)';
  console.log('answer:', alert);
  expect(/started/.test(alert) && !/Nothing was started/.test(alert), 'the validation started the problem');

  // follow the missions of this validation until the planted pull request is merged and released, or one of them ends badly
  await page.getByRole('link', { name: 'Missions' }).click();
  const begin = Date.now();
  const wanted = scenario.expect?.pullRequests ?? [];
  const merged = () => wanted.every((w) => gh('pr', 'view', w.branch, '-R', `${org}/${w.repo}`, '--json', 'state', '--jq', '.state') === w.state);
  let last = '', outcome = 'timeout', mine = [];
  while ((Date.now() - begin) / 1000 < maxSeconds) {
    mine = (await missionsOf()).filter((m) => !known.has(m.id));
    const labels = mine.map((m) => `${m.world} ${m.state}`).join(' | ');
    if (labels !== last) { console.log(`[${Math.round((Date.now() - begin) / 1000)}s] ${labels}`); last = labels; }
    if (mine.some((m) => m.state === 'Ended' || m.state === 'Refused' || m.state === 'Failed' || m.state === 'Stopped')) { outcome = 'ended badly'; break; }
    if (mine.some((m) => m.world === 'Release' && m.state === 'Accepted') && merged()) { outcome = 'released'; break; }
    await page.waitForTimeout(5000);
  }
  expect(outcome === 'released', `the chain went from staging to the release (${outcome}; last: ${last || 'nothing'})`);
  if (outcome !== 'released') for (const m of mine) console.log(`${m.world} ${m.state}: ${m.endReason ?? ''} ${(m.end ?? '').toString().slice(0, 300)}`);

  // 4. the final state, read from the forge and not from the interface
  for (const want of scenario.expect?.pullRequests ?? []) {
    const state = gh('pr', 'view', want.branch, '-R', `${org}/${want.repo}`, '--json', 'state', '--jq', '.state');
    expect(state === want.state, `${want.repo} ${want.branch} is ${want.state} (is ${state})`);
  }
  expect(problems.length === 0, problems.length ? 'page errors: ' + problems.join(' | ') : 'no page error, no failing API call');
  await browser.close();
  // 5. the sandbox goes back to its seed (LAB_KEEP=1 leaves what the run made, to look at it)
  if (!process.env.LAB_KEEP && !process.env.LAB_SKIP_SETUP) console.log(sandbox('reset', ...repos).trim());
  console.log(failures.length ? `\n${scenario.case}: NOT ticked (${failures.length} failure(s))` : `\n${scenario.case}: ticked`);
  process.exit(failures.length ? 1 : 0);
})().catch((e) => { console.error('failed:', e.message.split('\n')[0]); process.exit(1); });

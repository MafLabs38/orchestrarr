import { finding, step, workItem } from 'testUtils';
import type { Finding, PullRequest, WorkItem } from 'typings/api';

import { summarize, summarizePlan } from './summarize';

const f = (overrides: Partial<Finding>): Finding => ({ ...finding, ...overrides });
const item = (overrides: Partial<WorkItem>): WorkItem => ({ ...workItem, ...overrides });
const pr = (number: number, ready: boolean): PullRequest => ({
  number,
  title: 't',
  url: 'https://forge.example/o/r/pull/1',
  author: 'bot',
  headBranch: 'b',
  draft: false,
  checks: ready ? 'passing' : 'failing',
  merge: ready ? 'clean' : 'unstable',
  labels: [],
  changes: [],
  createdAt: '2026-11-01T09:38:16Z',
});

describe('summarize', () => {
  it('says the alerts with the worst ones, in a few words', () => {
    expect(
      summarize(item({ findings: [f({ count: 4, alerts: { critical: 1, high: 2, medium: 1, low: 0 } })] })),
    ).toEqual(['4 alerts (1 critical)']);
    expect(
      summarize(item({ findings: [f({ count: 3, alerts: { critical: 0, high: 2, medium: 1, low: 0 } })] })),
    ).toEqual(['3 alerts (2 high)']);
    expect(
      summarize(item({ findings: [f({ count: 1, alerts: { critical: 0, high: 0, medium: 0, low: 1 } })] })),
    ).toEqual(['1 alert']);
  });

  it('says code alerts, exposed secrets, a failing ci and an alert scan that is off', () => {
    const findings = [
      f({
        kind: 'codeScanningAlerts',
        severity: 'warning',
        count: 2,
        alerts: { critical: 0, high: 0, medium: 2, low: 0 },
      }),
      f({ kind: 'secretScanningAlerts', severity: 'danger', count: 1, alerts: null }),
      f({ kind: 'failingCi', severity: 'warning', count: 1, alerts: null }),
      f({ kind: 'dependencyScanUnavailable', severity: 'info', count: 1, alerts: null }),
    ];

    expect(summarize(item({ findings }))).toEqual([
      '1 exposed secret',
      '2 code alerts',
      'CI failing',
      'alerts not scanned',
    ]);
  });

  it('says how many pull requests wait, and how many are ready once they are read', () => {
    const waiting = f({ kind: 'openDependencyPullRequests', count: 3, alerts: null });

    expect(summarize(item({ findings: [waiting], pullRequests: null }))).toEqual(['3 PRs']);
    expect(summarize(item({ findings: [waiting], pullRequests: [pr(1, true), pr(2, true), pr(3, false)] }))).toEqual([
      '3 PRs (2 ready)',
    ]);
    expect(summarize(item({ findings: [{ ...waiting, count: 1 }], pullRequests: [pr(1, true)] }))).toEqual([
      '1 PR (1 ready)',
    ]);
  });

  it('says how far an image moved, when it is known', () => {
    const image = (change: 'major' | 'minor' | 'patch' | 'none' | 'build') =>
      f({
        kind: 'outdatedImage',
        count: 1,
        alerts: null,
        image: { reference: 'a:1', current: '1', latest: '2', change },
      });

    expect(summarize(item({ findings: [image('major')] }))).toEqual(['major image update']);
    expect(summarize(item({ findings: [image('patch')] }))).toEqual(['patch image update']);
    expect(summarize(item({ findings: [image('none')] }))).toEqual(['image update']);
    expect(summarize(item({ findings: [image('build')] }))).toEqual(['image update']);
  });

  it('puts the most serious first', () => {
    const findings = [
      f({ kind: 'failingCi', severity: 'warning', count: 1, alerts: null }),
      f({ count: 1, alerts: { critical: 1, high: 0, medium: 0, low: 0 } }),
    ];

    expect(summarize(item({ findings }))).toEqual(['1 alert (1 critical)', 'CI failing']);
  });
});

describe('summarizePlan', () => {
  it('says how many steps approving would take', () => {
    expect(
      summarizePlan(
        item({ plan: [step(), step({ kind: 'deploy', ticked: false, available: false, reason: 'noPlacement' })] }),
      ),
    ).toBe('1 of 2 steps');
  });

  it('says a person has to act when there is no plan', () => {
    expect(summarizePlan(item({ plan: [] }))).toBe('needs a person');
  });
});

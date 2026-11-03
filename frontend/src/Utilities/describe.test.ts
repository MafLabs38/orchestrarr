import { finding, step } from 'testUtils';
import type { Finding, SourceError } from 'typings/api';

import { describeError, describeFinding, describeReason, describeStep } from './describe';

const f = (overrides: Partial<Finding>): Finding => ({ ...finding, ...overrides });

describe('describeFinding', () => {
  it.each<[Partial<Finding>, string]>([
    [{ count: 1, alerts: { critical: 0, high: 1, medium: 0, low: 0 } }, '1 dependency alert (1 high)'],
    [{ count: 3, alerts: { critical: 1, high: 0, medium: 2, low: 0 } }, '3 dependency alerts (1 critical, 2 medium)'],
    [
      { kind: 'codeScanningAlerts', count: 2, alerts: { critical: 0, high: 0, medium: 0, low: 2 } },
      '2 code scanning alerts (2 low)',
    ],
    [{ kind: 'secretScanningAlerts', count: 1, alerts: null }, '1 exposed secret'],
    [{ kind: 'secretScanningAlerts', count: 2, alerts: null }, '2 exposed secrets'],
    [{ kind: 'openDependencyPullRequests', count: 1, alerts: null }, '1 open dependency pull request waiting'],
    [{ kind: 'failingCi', count: 1, alerts: null }, 'The CI of the default branch is failing'],
    [
      { kind: 'dependencyScanUnavailable', count: 1, alerts: null },
      'Dependency alerts are off or cannot be read: this repository is not known to be clean',
    ],
  ])('words a finding from its data', (overrides, expected) => {
    expect(describeFinding(f(overrides))).toBe(expected);
  });

  it.each([
    [
      { reference: 'app:1', current: '1.0.0', latest: '2.0.0', change: 'major' as const },
      'app:1: 1.0.0 → 2.0.0 (major)',
    ],
    [
      { reference: 'app:1', current: '1.0.0', latest: '1.0.1', change: 'patch' as const },
      'app:1: 1.0.0 → 1.0.1 (patch)',
    ],
    [{ reference: 'app:1', current: null, latest: null, change: 'none' as const }, 'app:1: a newer build exists'],
  ])('words an outdated image', (image, expected) => {
    expect(describeFinding(f({ kind: 'outdatedImage', alerts: null, image }))).toBe(expected);
  });
});

describe('describeStep', () => {
  it.each([
    [step({ kind: 'validate' }), 'Build and test the change'],
    [step({ kind: 'mergePullRequests' }), 'Merge the pull requests that pass'],
    [step({ kind: 'mergeFix' }), 'Merge the fix'],
    [step({ kind: 'deploy', environmentName: 'Server 1' }), 'Deploy to Server 1'],
    [step({ kind: 'deploy', environmentName: null }), 'Deploy'],
  ])('words a step', (planStep, expected) => {
    expect(describeStep(planStep)).toBe(expected);
  });
});

describe('describeReason and describeError', () => {
  it('words the reason a step cannot be done', () => {
    expect(describeReason('noPlacement')).toBe('The environment description does not say where this runs.');
  });

  it.each<SourceError>(['failed', 'unreachable', 'unauthorized', 'timeout', 'invalidResponse'])(
    'words the error %s with its own sentence',
    (error) => {
      expect(describeError(error)).toMatch(/^The source /);
    },
  );
});

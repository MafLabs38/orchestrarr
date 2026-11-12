import { problem } from 'testUtils';
import type { Ceiling, CeilingReason, ProblemKind, SourceError } from 'typings/api';

import {
  describeApprovalError,
  describeCeiling,
  describeError,
  describeEvent,
  describeProblem,
  describeProblemKind,
  describeStepKind,
  describeWhy,
} from './describe';

describe('describeError', () => {
  it.each<SourceError>(['failed', 'unreachable', 'unauthorized', 'timeout', 'invalidResponse'])(
    'words the error %s with its own sentence',
    (error) => {
      expect(describeError(error)).toMatch(/^The source /);
    },
  );
});

describe('the words of the table', () => {
  it.each<[Ceiling, string]>([
    ['noAction', 'No action'],
    ['qualification', 'Validate only'],
    ['repair', 'Validate, then AI repair'],
    ['mergeIntoDevelop', 'Validate, then merge'],
    ['production', 'Validate, merge, production'],
  ])('words how far a problem goes: %s', (ceiling, expected) => {
    expect(describeCeiling(ceiling)).toBe(expected);
  });

  it.each<CeilingReason>([
    'none',
    'noDescription',
    'noActionDefined',
    'pullRequestsNotCurrent',
    'notGranted',
    'noExecutor',
    'qualificationNotDescribed',
    'checksFailing',
    'checksNotPassing',
    'cannotBeMergedNow',
    'noRollbackDescribed',
    'noCheckAfterDeployDescribed',
    'updateClassStopsBeforeProduction',
  ])('has a sentence for the reason %s', (reason) => {
    expect(describeWhy(reason).length).toBeGreaterThan(10);
  });

  it.each<[ProblemKind, string]>([
    ['dependencyUpdate', 'Dependency update'],
    ['dependencyAlert', 'Dependency alert'],
    ['codeScanningAlert', 'Code scanning alert'],
    ['exposedSecret', 'Exposed secret'],
    ['failingCi', 'CI failing'],
    ['imageUpdate', 'Image update'],
    ['dependencyScanUnavailable', 'Dependency scan unavailable'],
    ['pullRequestsNotRead', 'Pull requests not read'],
  ])('words the kind %s', (kind, expected) => {
    expect(describeProblemKind(kind)).toBe(expected);
  });

  it('words a problem from its data: the update as the server gives it, the others from a count', () => {
    expect(describeProblem(problem())).toBe('express 4.16.0 → 4.21.2');
    expect(describeProblem(problem({ kind: 'dependencyAlert', count: 3 }))).toBe('3 alerts');
    expect(describeProblem(problem({ kind: 'codeScanningAlert', count: 1 }))).toBe('1 alerts');
    expect(describeProblem(problem({ kind: 'exposedSecret', count: 2 }))).toBe('2 exposed');
    expect(describeProblem(problem({ kind: 'pullRequestsNotRead', count: 4 }))).toBe('4 waiting, not read yet');
    expect(describeProblem(problem({ kind: 'failingCi' }))).toBe('The build fails');
    expect(describeProblem(problem({ kind: 'dependencyScanUnavailable' }))).toMatch(/scan is off or unreadable/);
    expect(describeProblem(problem({ kind: 'imageUpdate', description: 'ghcr.io/example/app:1 → 2' }))).toBe(
      'ghcr.io/example/app:1 → 2',
    );
  });
});

describe('the other words', () => {
  it('words a kind of step, an event of the trail and a refusal of an approval', () => {
    expect(describeStepKind('mergePullRequests')).toBe('Merge the pull requests that pass');
    expect(describeEvent('killswitch.engaged')).toBe('Global stop engaged');
    expect(describeEvent('something.new')).toBe('something.new');
    expect(describeApprovalError('stepNotGranted')).toBe('A ticked step is not granted to Orchestrarr.');
  });

  it('gives a sentence even for a refusal it does not know, or none', () => {
    expect(describeApprovalError('brandNew')).toBe('The approval was refused.');
    expect(describeApprovalError('tooManyRequests')).toBe('Too many attempts: wait a minute and try again.');
    expect(describeApprovalError(null)).toBe('The approval was refused.');
    expect(describeApprovalError('constructor')).toBe('The approval was refused.');
    expect(describeEvent('toString')).toBe('toString');
  });
});

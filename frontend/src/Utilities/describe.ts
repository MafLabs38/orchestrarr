import type { AlertCounts, Finding, PlanStep, SourceError, StepReason } from 'typings/api';

import translate from './translate';

const severities: (keyof AlertCounts)[] = ['critical', 'high', 'medium', 'low'];

const alertWords = {
  critical: 'AlertCritical',
  high: 'AlertHigh',
  medium: 'AlertMedium',
  low: 'AlertLow',
} as const;

const changeWords = { build: 'ChangeBuild', patch: 'ChangePatch', minor: 'ChangeMinor', major: 'ChangeMajor' } as const;

function breakdown(alerts: AlertCounts | null): string {
  if (!alerts) {
    return '';
  }
  return severities
    .filter((severity) => alerts[severity] > 0)
    .map((severity) => translate(alertWords[severity], { count: alerts[severity] }))
    .join(', ');
}

function image(finding: Finding): string {
  const update = finding.image;
  if (!update) {
    return '';
  }
  const move =
    update.current && update.latest
      ? translate('ImageMove', { current: update.current, latest: update.latest })
      : translate('ImageNewerBuild');
  const change =
    update.change === 'none' ? '' : translate('ImageChange', { change: translate(changeWords[update.change]) });
  return translate('FindingOutdatedImage', { reference: update.reference, move, change });
}

// The sentence of a finding, from its data.
export function describeFinding(finding: Finding): string {
  const tokens = { count: finding.count, breakdown: breakdown(finding.alerts) };
  const one = finding.count === 1;
  switch (finding.kind) {
    case 'dependencyAlerts':
      return translate(one ? 'FindingDependencyAlertsOne' : 'FindingDependencyAlertsOther', tokens);
    case 'codeScanningAlerts':
      return translate(one ? 'FindingCodeScanningAlertsOne' : 'FindingCodeScanningAlertsOther', tokens);
    case 'secretScanningAlerts':
      return translate(one ? 'FindingSecretScanningAlertsOne' : 'FindingSecretScanningAlertsOther', tokens);
    case 'openDependencyPullRequests':
      return translate(one ? 'FindingOpenDependencyPullRequestsOne' : 'FindingOpenDependencyPullRequestsOther', tokens);
    case 'failingCi':
      return translate('FindingFailingCi');
    case 'outdatedImage':
      return image(finding);
    case 'dependencyScanUnavailable':
      return translate('FindingDependencyScanUnavailable');
  }
}

// The label of a plan step.
export function describeStep(step: PlanStep): string {
  switch (step.kind) {
    case 'validate':
      return translate('StepValidate');
    case 'mergePullRequests':
      return translate('StepMergePullRequests');
    case 'mergeFix':
      return translate('StepMergeFix');
    case 'deploy':
      return step.environmentName
        ? translate('StepDeploy', { environment: step.environmentName })
        : translate('StepDeployAnywhere');
  }
}

const reasons = { noPlacement: 'ReasonNoPlacement' } as const satisfies Record<StepReason, string>;

export function describeReason(reason: StepReason): string {
  return translate(reasons[reason]);
}

const errors = {
  failed: 'ErrorFailed',
  unreachable: 'ErrorUnreachable',
  unauthorized: 'ErrorUnauthorized',
  timeout: 'ErrorTimeout',
  invalidResponse: 'ErrorInvalidResponse',
} as const satisfies Record<SourceError, string>;

export function describeError(error: SourceError): string {
  return translate(errors[error]);
}

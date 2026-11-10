import type {
  AlertCounts,
  ApprovalError,
  ApprovalState,
  BatchState,
  ChecksState,
  Finding,
  MergeState,
  PlanStep,
  PullRequest,
  RunRefusal,
  SourceError,
  StepKind,
  StepReason,
  TaskKind,
  TaskResult,
  UpdateKind,
} from 'typings/api';

import translate from './translate';

// The words of the interface for what the server sends as data and codes (SPEC D-22).
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

const stepKinds = {
  validate: 'StepValidate',
  mergePullRequests: 'StepMergePullRequests',
  mergeFix: 'StepMergeFix',
  deploy: 'StepDeployAnywhere',
} as const satisfies Record<StepKind, string>;

// The label of a kind of step, where no environment is shown (the queue).
export function describeStepKind(kind: StepKind): string {
  return translate(stepKinds[kind]);
}

const events = {
  'approval.recorded': 'EventApprovalRecorded',
  'approval.refused': 'EventApprovalRefused',
  'killswitch.engaged': 'EventKillSwitchEngaged',
  'killswitch.released': 'EventKillSwitchReleased',
  'instance.created': 'EventInstanceCreated',
  'batch.queued': 'EventBatchQueued',
  'batch.delivered': 'EventBatchDelivered',
  'batch.done': 'EventBatchDone',
  'batch.refused': 'EventBatchRefused',
  'batch.expired': 'EventBatchExpired',
  'login.succeeded': 'EventLoginSucceeded',
  'login.failed': 'EventLoginFailed',
  'login.locked': 'EventLoginLocked',
  'password.locked': 'EventPasswordLocked',
  'password.verify-failed': 'EventPasswordVerifyFailed',
  'account.created': 'EventAccountCreated',
  'account.role-changed': 'EventAccountRoleChanged',
  'account.enabled': 'EventAccountEnabled',
  'account.disabled': 'EventAccountDisabled',
  'account.password-changed': 'EventAccountPasswordChanged',
  'account.reset': 'EventAccountReset',
  'account.deleted': 'EventAccountDeleted',
} as const;

// The words of an event of the audit trail; one the interface does not know is shown as the server names it.
export function describeEvent(event: string): string {
  return Object.hasOwn(events, event) ? translate(events[event as keyof typeof events]) : event;
}

const approvalErrors = {
  notSignedIn: 'ApprovalErrorNotSignedIn',
  notAnOperator: 'ApprovalErrorNotAnOperator',
  notAPerson: 'ApprovalErrorNotAPerson',
  killSwitchEngaged: 'ApprovalErrorKillSwitchEngaged',
  unknownWorkItem: 'ApprovalErrorUnknownWorkItem',
  pullRequestsNotRead: 'ApprovalErrorPullRequestsNotRead',
  nothingTicked: 'ApprovalErrorNothingTicked',
  planChanged: 'ApprovalErrorPlanChanged',
  stepNotOffered: 'ApprovalErrorStepNotOffered',
  stepNotGranted: 'ApprovalErrorStepNotGranted',
  stepNotSupported: 'ApprovalErrorStepNotSupported',
  mergeNeedsValidation: 'ApprovalErrorMergeNeedsValidation',
  noPullRequests: 'ApprovalErrorNoPullRequests',
  alreadyApproved: 'ApprovalErrorAlreadyApproved',
  secondFactorRequired: 'ApprovalErrorSecondFactorRequired',
  storeFull: 'ApprovalErrorStoreFull',
  auditUnavailable: 'ApprovalErrorAuditUnavailable',
  tooManyRequests: 'ApprovalErrorTooManyRequests',
} as const satisfies Record<ApprovalError, string>;

// Why an approval was refused, from the code of the server; a code the interface does not know still gets a sentence.
export function describeApprovalError(code: string | null): string {
  return code !== null && Object.hasOwn(approvalErrors, code)
    ? translate(approvalErrors[code as ApprovalError])
    : translate('ApprovalErrorUnknown');
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
  rateLimited: 'ErrorRateLimited',
} as const satisfies Record<SourceError, string>;

export function describeError(error: SourceError): string {
  return translate(errors[error]);
}

const checks = {
  passing: 'ChecksPassing',
  failing: 'ChecksFailing',
  pending: 'ChecksPending',
  unknown: 'ChecksUnknown',
} as const satisfies Record<ChecksState, string>;

export function describeChecks(state: ChecksState): string {
  return translate(checks[state]);
}

const merges = {
  clean: 'MergeClean',
  unstable: 'MergeUnstable',
  behind: 'MergeBehind',
  blocked: 'MergeBlocked',
  conflicting: 'MergeConflicting',
  draft: 'MergeDraft',
  unknown: 'MergeUnknown',
} as const satisfies Record<MergeState, string>;

export function describeMerge(state: MergeState): string {
  return translate(merges[state]);
}

// The word of a kind of change: "major"
export function describeChangeWord(kind: Exclude<UpdateKind, 'unknown'>): string {
  return translate(changeWords[kind]);
}

// "major update", or nothing when the kind is not known
export function describeUpdateKind(kind: UpdateKind): string | null {
  return kind === 'unknown' ? null : translate('UpdateKind', { kind: translate(changeWords[kind]) });
}

// What a dependency pull request updates, from the parsed title: "lodash 4.17.4 → 4.17.21", or the packages, or the title itself.
export function describeChanges(pullRequest: PullRequest): string {
  const [first] = pullRequest.changes;
  if (pullRequest.changes.length === 1 && first.from && first.to) {
    return `${first.package} ${translate('ImageMove', { current: first.from, latest: first.to })}`;
  }
  return pullRequest.changes.length > 0
    ? pullRequest.changes.map((change) => change.package).join(', ')
    : pullRequest.title;
}

// The most significant kind of a pull request's changes, which says how risky it is
export function updateKindOf(pullRequest: PullRequest): UpdateKind {
  const order: UpdateKind[] = ['major', 'minor', 'patch'];
  return order.find((kind) => pullRequest.changes.some((change) => change.kind === kind)) ?? 'unknown';
}

const approvalStates = {
  waiting: 'ApprovalStateWaiting',
  taken: 'ApprovalStateTaken',
  dryRunDone: 'ApprovalStateDryRunDone',
  refused: 'ApprovalStateRefused',
  expired: 'ApprovalStateExpired',
} as const satisfies Record<ApprovalState, string>;

export function describeApprovalState(state: ApprovalState): string {
  return translate(approvalStates[state]);
}

const batchStates = {
  waiting: 'BatchWaiting',
  delivered: 'BatchDelivered',
  done: 'BatchDone',
  refused: 'BatchRefused',
  expired: 'BatchExpired',
} as const satisfies Record<BatchState, string>;

export function describeBatchState(state: BatchState): string {
  return translate(batchStates[state]);
}

const refusals = {
  stopped: 'RefusalStopped',
  expired: 'RefusalExpired',
  replayed: 'RefusalReplayed',
  secondFactor: 'RefusalSecondFactor',
  delegationInvalid: 'RefusalDelegationInvalid',
  kindNotAllowed: 'RefusalKindNotAllowed',
  repositoryNotAllowed: 'RefusalRepositoryNotAllowed',
  journalMismatch: 'RefusalJournalMismatch',
  malformed: 'RefusalMalformed',
} as const satisfies Record<RunRefusal, string>;

// Why the executor refused a batch, from its code; a code the interface does not know is shown as the server names it.
export function describeRunRefusal(refusal: string): string {
  return translate('RunRefusedBecause', {
    reason: Object.hasOwn(refusals, refusal) ? translate(refusals[refusal as RunRefusal]) : refusal,
  });
}

const taskKinds = {
  validatePullRequest: 'TaskKindValidatePullRequest',
  mergePullRequest: 'TaskKindMergePullRequest',
} as const satisfies Record<TaskKind, string>;

export function describeTaskKind(kind: TaskKind): string {
  return translate(taskKinds[kind]);
}

const taskResults = {
  dryRun: 'TaskResultDryRun',
  refused: 'TaskResultRefused',
  skipped: 'TaskResultSkipped',
} as const satisfies Record<TaskResult, string>;

export function describeTaskResult(result: TaskResult | null): string {
  return result === null ? translate('TaskNotRunYet') : translate(taskResults[result]);
}

const runEvents = {
  'run.claimed': 'RunEventClaimed',
  'run.stopped': 'RunEventStopped',
  'run.batch-checked': 'RunEventBatchChecked',
  'run.second-factor': 'RunEventSecondFactor',
  'run.delegation': 'RunEventDelegation',
  'run.rights': 'RunEventRights',
  'run.double-control': 'RunEventDoubleControl',
  'task.dry-run': 'RunEventTaskDryRun',
  'task.refused': 'RunEventTaskRefused',
  'task.skipped': 'RunEventTaskSkipped',
  'run.completed': 'RunEventCompleted',
  'run.refused': 'RunEventRefused',
} as const;

// The words of a line of the trace of a run; one the interface does not know is shown as the executor names it.
export function describeRunEvent(type: string): string {
  return Object.hasOwn(runEvents, type) ? translate(runEvents[type as keyof typeof runEvents]) : type;
}

import type {
  ApprovalError,
  ApprovalState,
  BatchState,
  Ceiling,
  CeilingReason,
  ChecksState,
  ProblemKind,
  ProblemRow,
  RunRefusal,
  Severity,
  SourceError,
  StepKind,
  TaskKind,
  TaskResult,
  WorkshopVerdict,
} from 'typings/api';

import translate from './translate';

// The words of the interface for what the server sends as data and codes (SPEC D-22).
const stepKinds = {
  validate: 'StepValidate',
  mergePullRequests: 'StepMergePullRequests',
  mergeFix: 'StepMergeFix',
  deploy: 'StepDeployAnywhere',
  repair: 'StepRepair',
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
  'option.changed': 'EventOptionChanged',
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
  tooManyPullRequests: 'ApprovalErrorTooManyPullRequests',
  tooManyRepairs: 'ApprovalErrorTooManyRepairs',
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

const approvalStates = {
  waiting: 'ApprovalStateWaiting',
  taken: 'ApprovalStateTaken',
  ended: 'ApprovalStateEnded',
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

// Why the runner refused a batch, from its code; a code the interface does not know is shown as the server names it.
export function describeRunRefusal(refusal: string): string {
  return translate('RunRefusedBecause', {
    reason: Object.hasOwn(refusals, refusal) ? translate(refusals[refusal as RunRefusal]) : refusal,
  });
}

const taskKinds = {
  validatePullRequest: 'TaskKindValidatePullRequest',
  mergePullRequest: 'TaskKindMergePullRequest',
  repairPullRequest: 'TaskKindRepairPullRequest',
} as const satisfies Record<TaskKind, string>;

export function describeTaskKind(kind: TaskKind): string {
  return translate(taskKinds[kind]);
}

const taskResults = {
  dryRun: 'TaskResultDryRun',
  refused: 'TaskResultRefused',
  skipped: 'TaskResultSkipped',
  passed: 'TaskResultPassed',
  failed: 'TaskResultFailed',
  repaired: 'TaskResultRepaired',
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
  'task.started': 'RunEventTaskStarted',
  'task.step': 'RunEventTaskStep',
  'task.passed': 'RunEventTaskPassed',
  'task.patch': 'RunEventTaskPatch',
  'task.agent': 'RunEventTaskAgent',
  'task.failed': 'RunEventTaskFailed',
  'task.refused': 'RunEventTaskRefused',
  'task.skipped': 'RunEventTaskSkipped',
  'run.completed': 'RunEventCompleted',
  'run.refused': 'RunEventRefused',
} as const;

// The words of a line of the trace of a run; one the interface does not know is shown as the runner names it.
export function describeRunEvent(type: string): string {
  return Object.hasOwn(runEvents, type) ? translate(runEvents[type as keyof typeof runEvents]) : type;
}

const ceilings = {
  noAction: 'GoesUpNoAction',
  qualification: 'GoesUpQualification',
  mergeIntoDevelop: 'GoesUpMergeIntoDevelop',
  production: 'GoesUpProduction',
  repair: 'GoesUpRepair',
} as const satisfies Record<Ceiling, string>;

export function describeCeiling(ceiling: Ceiling): string {
  return translate(ceilings[ceiling]);
}

const whys = {
  none: 'WhyNone',
  noDescription: 'WhyNoDescription',
  noActionDefined: 'WhyNoActionDefined',
  pullRequestsNotCurrent: 'WhyPullRequestsNotCurrent',
  notGranted: 'WhyNotGranted',
  noRunner: 'WhyNoRunner',
  qualificationNotDescribed: 'WhyQualificationNotDescribed',
  checksFailing: 'WhyChecksFailing',
  checksNotPassing: 'WhyChecksNotPassing',
  cannotBeMergedNow: 'WhyCannotBeMergedNow',
  noRollbackDescribed: 'WhyNoRollbackDescribed',
  noCheckAfterDeployDescribed: 'WhyNoCheckAfterDeployDescribed',
  updateClassStopsBeforeProduction: 'WhyUpdateClassStopsBeforeProduction',
  qualificationFailed: 'WhyQualificationFailed',
  repairFailed: 'WhyRepairFailed',
  patchReady: 'WhyPatchReady',
  passesHereChecksFailing: 'WhyPassesHereChecksFailing',
} as const satisfies Record<CeilingReason, string>;

// Why a problem stops where it does, from the code of the server
export function describeWhy(reason: CeilingReason): string {
  return translate(whys[reason]);
}

const problemKinds = {
  dependencyUpdate: 'KindDependencyUpdate',
  dependencyAlert: 'KindDependencyAlert',
  codeScanningAlert: 'KindCodeScanningAlert',
  exposedSecret: 'KindExposedSecret',
  failingCi: 'KindFailingCi',
  imageUpdate: 'KindImageUpdate',
  dependencyScanUnavailable: 'KindDependencyScanUnavailable',
  pullRequestsNotRead: 'KindPullRequestsNotRead',
} as const satisfies Record<ProblemKind, string>;

export function describeProblemKind(kind: ProblemKind): string {
  return translate(problemKinds[kind]);
}

// The sentence of a problem, from its data: the server sends a dependency update as "package from → to" and the others as a count or an image move
export function describeProblem(row: ProblemRow): string {
  const count = row.count ?? 0;
  switch (row.kind) {
    case 'dependencyAlert':
    case 'codeScanningAlert':
      return translate('ProblemAlerts', { count });
    case 'exposedSecret':
      return translate('ProblemSecrets', { count });
    case 'pullRequestsNotRead':
      return translate('ProblemPullRequestsWaiting', { count });
    case 'failingCi':
      return translate('ProblemFailingCi');
    case 'dependencyScanUnavailable':
      return translate('ProblemScanUnavailable');
    default:
      return row.description;
  }
}

// The same, in two to four words, for the column of the table: the sentence is in the bubble beside it
const whyShorts = {
  none: 'WhyShortNone',
  noDescription: 'WhyShortNoDescription',
  noActionDefined: 'WhyShortNoActionDefined',
  pullRequestsNotCurrent: 'WhyShortPullRequestsNotCurrent',
  notGranted: 'WhyShortNotGranted',
  noRunner: 'WhyShortNoRunner',
  qualificationNotDescribed: 'WhyShortQualificationNotDescribed',
  checksFailing: 'WhyShortChecksFailing',
  checksNotPassing: 'WhyShortChecksNotPassing',
  cannotBeMergedNow: 'WhyShortCannotBeMergedNow',
  noRollbackDescribed: 'WhyShortNoRollbackDescribed',
  noCheckAfterDeployDescribed: 'WhyShortNoCheckAfterDeployDescribed',
  updateClassStopsBeforeProduction: 'WhyShortUpdateClassStopsBeforeProduction',
  qualificationFailed: 'WhyShortQualificationFailed',
  repairFailed: 'WhyShortRepairFailed',
  patchReady: 'WhyShortPatchReady',
  passesHereChecksFailing: 'WhyShortPassesHereChecksFailing',
} as const satisfies Record<CeilingReason, string>;

export function describeWhyShort(reason: CeilingReason): string {
  return translate(whyShorts[reason]);
}

const problemKindShorts = {
  dependencyUpdate: 'KindShortDependencyUpdate',
  dependencyAlert: 'KindShortDependencyAlert',
  codeScanningAlert: 'KindShortCodeScanningAlert',
  exposedSecret: 'KindShortExposedSecret',
  failingCi: 'KindShortFailingCi',
  imageUpdate: 'KindShortImageUpdate',
  dependencyScanUnavailable: 'KindShortDependencyScanUnavailable',
  pullRequestsNotRead: 'KindShortPullRequestsNotRead',
} as const satisfies Record<ProblemKind, string>;

export function describeProblemKindShort(kind: ProblemKind): string {
  return translate(problemKindShorts[kind]);
}

const workshops = {
  unknown: 'WorkshopUnknown',
  passes: 'WorkshopPasses',
  fails: 'WorkshopFails',
  repaired: 'WorkshopRepaired',
} as const satisfies Record<WorkshopVerdict, string>;

export function describeWorkshop(verdict: WorkshopVerdict): string {
  return translate(workshops[verdict]);
}

const severities = {
  danger: 'SeverityDanger',
  warning: 'SeverityWarning',
  info: 'SeverityInfo',
} as const satisfies Record<Severity, string>;

export function describeSeverity(severity: Severity): string {
  return translate(severities[severity]);
}

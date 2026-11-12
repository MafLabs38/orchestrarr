// The resources of the Orchestrarr API (GET /api/v1/...). Enums are camelCase strings. The server sends data and codes; the
// interface words them (Utilities/describe.ts).

export type Severity = 'info' | 'warning' | 'danger';

export type StepKind = 'validate' | 'mergePullRequests' | 'mergeFix' | 'deploy' | 'repair';

// notRun: the checks that fail are the ones that cannot run for a bot's pull request (they need secrets)
export type ChecksState = 'unknown' | 'pending' | 'passing' | 'failing' | 'notRun';

export type UpdateKind = 'unknown' | 'patch' | 'minor' | 'major' | 'digest' | 'group';

export type SourceError = 'failed' | 'unreachable' | 'unauthorized' | 'timeout' | 'invalidResponse' | 'rateLimited';

export interface SystemStatus {
  name: string;
  version: string;
  source: { kind: string; configured: boolean; readAt: string | null; error: SourceError | null; stale: boolean };
  forge: { kind: string; configured: boolean };
  approvals: ApprovalStatus;
  storage: StorageStatus;
  // The runners that asked for work: what they say of themselves, and whether they did so lately
  runners: RunnerStatus[];
}

export type TaskKind = 'validatePullRequest' | 'mergePullRequest' | 'repairPullRequest';

export interface DelegationInfo {
  id: string;
  subject: string;
  kind: TaskKind;
  repository: string;
  expiresAt: string;
  dailyQuota: number;
}

export interface RunnerStatus {
  name: string;
  version: string;
  lastSeen: string;
  connected: boolean;
  kinds: TaskKind[];
  repositories: string[];
  delegations: DelegationInfo[];
  // Whether this runner checks the code of the authenticator (false only in development, while every runner changes nothing outside its sandbox)
  secondFactorRequired: boolean;
}

// Where the approvals, the trail and the global stop are kept: "sqlite" survives a restart, "memory" does not.
export type StorageKind = 'sqlite' | 'memory';

export interface StorageStatus {
  kind: StorageKind;
}

// Who is signed in as an approver (the identifier that counts, and the
// name to show), and whether the global stop is engaged.
export interface ApprovalStatus {
  operator: string | null;
  operatorName: string | null;
  // Only an operator is offered approving and the global stop
  isOperator: boolean;
  killSwitchEngaged: boolean;
  // False when the stop is engaged but the file could not take it: it holds until a restart, which would release it
  killSwitchSaved: boolean;
  // Approving asks for the code of the authenticator app, which the runner checks (not this server)
  requiresSecondFactor: boolean;
}

// Who is signed in (GET /auth/me): the subject is the identifier the approvals and the authenticator's enrolment use for this person.
export type RoleName = 'Admin' | 'Operator' | 'Reader';

export interface Session {
  subject: string;
  name: string;
  role: RoleName;
  permissions: string[];
}

export interface Account {
  id: string;
  name: string;
  role: RoleName;
  enabled: boolean;
  createdAt: string;
}

export interface Role {
  name: RoleName;
  description: string;
  permissions: string[];
}

// Where an approval is in its life: a dry run spends it, so a real run needs a new approval.
export type ApprovalState = 'waiting' | 'taken' | 'ended' | 'refused' | 'expired';

// A decision of the operator, recorded: these steps on this pull request at this commit. It makes a batch of tasks that the runner takes.
export interface Approval {
  id: string;
  workItemId: string;
  repository: string;
  pullRequest: number;
  headSha: string;
  steps: StepKind[];
  approver: string;
  approvedAt: string;
  state: ApprovalState;
  batchId: string | null;
  endedAt: string | null;
}

export type BatchState = 'waiting' | 'delivered' | 'done' | 'refused' | 'expired';

export type TaskResult = 'dryRun' | 'refused' | 'skipped' | 'passed' | 'failed' | 'repaired';

// Why the runner refused a batch: a code, which the interface words.
export type RunRefusal =
  | 'stopped'
  | 'expired'
  | 'replayed'
  | 'secondFactor'
  | 'delegationInvalid'
  | 'kindNotAllowed'
  | 'repositoryNotAllowed'
  | 'journalMismatch'
  | 'malformed';

export interface RunSummary {
  batchId: string;
  workItemId: string;
  approverSubject: string;
  createdAt: string;
  state: BatchState;
  refusal: RunRefusal | null;
  taskCount: number;
  endedAt: string | null;
  runner: string | null;
}

export interface TaskSpec {
  id: string;
  sequence: number;
  kind: TaskKind;
  repository: string;
  pullRequest: number;
  headSha: string;
  approvalId: string;
}

export interface RunTask {
  spec: TaskSpec;
  result: TaskResult | null;
  reason: string | null;
}

// One line of the trace of a run, chained to the one before it: a line that was changed or removed afterwards breaks the chain.
export interface RunEvent {
  sequence: number;
  at: string;
  type: string;
  taskId: string | null;
  detail: string | null;
  previousHash: string;
  hash: string;
}

export interface RunDetail {
  summary: RunSummary;
  tasks: RunTask[];
  events: RunEvent[];
}

export interface ActivityEntry {
  id: number;
  at: string;
  event: string;
  subject: string;
  reference: string | null;
  detail: string | null;
}

// Why an approval was refused: a code, which the interface words (Utilities/describe.ts).
export type ApprovalError =
  | 'notSignedIn'
  | 'notAnOperator'
  | 'notAPerson'
  | 'killSwitchEngaged'
  | 'unknownWorkItem'
  | 'pullRequestsNotRead'
  | 'nothingTicked'
  | 'planChanged'
  | 'stepNotOffered'
  | 'stepNotGranted'
  | 'stepNotSupported'
  | 'mergeNeedsValidation'
  | 'noPullRequests'
  | 'alreadyApproved'
  | 'tooManyPullRequests'
  | 'tooManyRepairs'
  | 'secondFactorRequired'
  | 'storeFull'
  | 'auditUnavailable'
  | 'tooManyRequests';

// An option of the product (GET /options). "lockedReason" says why it cannot be turned off: the host was not started to allow it, or the configuration fixes it.
export interface OptionState {
  id: string;
  value: boolean;
  default: boolean;
  lowersSecurityWhenOff: boolean;
  canTurnOff: boolean;
  lockedReason: 'host' | 'configuration' | null;
}

// How far a problem can go by itself, and why it stops there (ADR 0019): computed by the server from the description of the application,
// what the connected runners accept and the state of the problem.
export type Ceiling = 'noAction' | 'qualification' | 'mergeIntoDevelop' | 'production' | 'repair';

export type CeilingReason =
  | 'none'
  | 'noDescription'
  | 'noActionDefined'
  | 'pullRequestsNotCurrent'
  | 'notGranted'
  | 'noRunner'
  | 'qualificationNotDescribed'
  | 'checksFailing'
  | 'checksNotPassing'
  | 'cannotBeMergedNow'
  | 'noRollbackDescribed'
  | 'noCheckAfterDeployDescribed'
  | 'updateClassStopsBeforeProduction'
  | 'qualificationFailed'
  | 'repairFailed'
  | 'patchReady'
  | 'passesHereChecksFailing'
  | 'checksNotRun'
  | 'notLongTermSupport'
  | 'managedByPlatform'
  | 'majorNeedsPlan'
  | 'notInScope'
  | 'managedByAutomation'
  | 'codeDiffersFromRunning'
  | 'codeChangeShown'
  | 'severalEnvironments'
  | 'platformUpgradeIsPlanned'
  | 'platformVersionNotRead';

// What the workshop last said about this very commit: the column beside the forge's checks
export type WorkshopVerdict = 'unknown' | 'passes' | 'fails' | 'repaired';

export type ProblemKind =
  | 'dependencyUpdate'
  | 'dependencyAlert'
  | 'codeScanningAlert'
  | 'exposedSecret'
  | 'failingCi'
  | 'imageUpdate'
  | 'dependencyScanUnavailable'
  | 'floatingImage'
  | 'dormantRepository'
  | 'platformUpdate'
  | 'pullRequestsNotRead';

// One problem, one row. A dependency update carries its pull request and the commit that was read; an alert has a severity and no action yet.
export interface ProblemRow {
  id: string;
  workItemId: string;
  application: string;
  kind: ProblemKind;
  description: string;
  severity: Severity | null;
  count: number | null;
  updateClass: UpdateKind | null;
  checks: ChecksState | null;
  url: string | null;
  pullRequest: number | null;
  headSha: string | null;
  goesUpTo: Ceiling;
  why: CeilingReason;
  canStopBeforeProduction: boolean;
  workshop: WorkshopVerdict;
  inProgress: boolean;
  tab: ProblemTab;
  // The application a person knows, as the source of the fleet names it, and the name of its icon; the repository or the image stays in `source`
  applicationId: string | null;
  icon: string | null;
  source: string | null;
  // Where the version is written in the code of the scope, and the version a change would write (ADR 0028)
  locations: CodeLocation[] | null;
  proposedTag: string | null;
}

// A place in the code of the scope that writes a version: the repository, the file, the line; `managed` names the automation that rewrites it (Flux)
export interface CodeLocation {
  repository: string;
  path: string;
  line: number;
  form: 'imageLine' | 'kustomizeTag' | 'terraformImage' | 'variable';
  image: { name: string; tag: string | null };
  managed: string | null;
}

// The code of the scope as the table found it, and what could not be read of it
export interface CodeScopeStatus {
  inScope: boolean;
  repositories: number;
  files: number;
  readAt: string | null;
  stale: boolean;
  error: string | null;
  problems: { repository: string; reason: string }[];
}

// A change to a file of the scope, computed and not applied
export interface CodeChange {
  repository: string;
  path: string;
  line: number;
  before: string;
  after: string;
  diff: string;
}

export interface CodeChangeReport {
  rowId: string;
  tag: string | null;
  changes: CodeChange[];
  left: CodeLocation[];
}

// Where a row is shown: what can be decided now, what needs a setting, what is only to know
export type ProblemTab = 'act' | 'configure' | 'know';

export type HistoryState = 'approved' | 'skipped';

export type HistoryOutcome =
  'skipped' | 'inProgress' | 'passed' | 'failed' | 'repaired' | 'refused' | 'expired' | 'ended';

// A line of the history: the photograph of the row when it was approved or set aside, and what became of it
export interface HistoryLine {
  id: number;
  state: HistoryState;
  outcome: HistoryOutcome;
  application: string;
  kind: ProblemKind;
  description: string;
  repository: string | null;
  pullRequest: number | null;
  headSha: string | null;
  batchId: string | null;
  at: string;
  by: string;
  reason: string | null;
}

export interface HistoryReport {
  lines: HistoryLine[];
  next: number | null;
}

export interface ProblemsReport {
  readAt: string | null;
  sourceKind: string;
  stale: boolean;
  error: SourceError | null;
  descriptionsVersion: string;
  rows: ProblemRow[];
  skipped: number;
  code: CodeScopeStatus | null;
}

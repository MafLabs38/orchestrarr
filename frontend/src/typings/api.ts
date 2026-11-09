// The resources of the Orchestrarr API (GET /api/v1/...). Enums are camelCase strings. The server sends data and codes; the
// interface words them (Utilities/describe.ts).

export type Severity = 'info' | 'warning' | 'danger';

export type FindingKind =
  | 'dependencyAlerts'
  | 'codeScanningAlerts'
  | 'secretScanningAlerts'
  | 'openDependencyPullRequests'
  | 'failingCi'
  | 'outdatedImage'
  | 'dependencyScanUnavailable';

export interface AlertCounts {
  critical: number;
  high: number;
  medium: number;
  low: number;
}

export type ImageChange = 'none' | 'build' | 'patch' | 'minor' | 'major';

export interface ImageUpdate {
  reference: string;
  current: string | null;
  latest: string | null;
  change: ImageChange;
}

export interface Finding {
  kind: FindingKind;
  severity: Severity;
  count: number;
  alerts: AlertCounts | null;
  image: ImageUpdate | null;
}

export type StepKind = 'validate' | 'mergePullRequests' | 'mergeFix' | 'deploy';

export type StepReason = 'noPlacement';

export interface PlanStep {
  kind: StepKind;
  environmentId: string | null;
  environmentName: string | null;
  ticked: boolean;
  available: boolean;
  granted: boolean;
  reason: StepReason | null;
}

export interface Placement {
  environmentId: string;
  environmentName: string;
  namespace: string | null;
  workloadName: string;
}

export type ChecksState = 'unknown' | 'pending' | 'passing' | 'failing';

export type MergeState = 'unknown' | 'clean' | 'unstable' | 'behind' | 'blocked' | 'conflicting' | 'draft';

export type UpdateKind = 'unknown' | 'patch' | 'minor' | 'major';

export interface DependencyChange {
  package: string;
  from: string | null;
  to: string | null;
  kind: UpdateKind;
}

export interface PullRequest {
  number: number;
  title: string;
  url: string;
  author: string;
  headBranch: string;
  draft: boolean;
  checks: ChecksState;
  merge: MergeState;
  labels: string[];
  changes: DependencyChange[];
  createdAt: string;
  headSha: string;
  readyToMerge: boolean;
}

export interface WorkItem {
  id: string;
  title: string;
  repositoryFullName: string | null;
  appIds: string[];
  severity: Severity;
  findings: Finding[];
  plan: PlanStep[];
  placements: Placement[];
  pullRequests: PullRequestsRead;
}

export type PullRequestsStatus = 'notNeeded' | 'notConnected' | 'pending' | 'read' | 'failed';

// The pull requests of a work item and the state of their reading. On a failure the items are the last good read, and stale says so.
export interface PullRequestsRead {
  status: PullRequestsStatus;
  items: PullRequest[];
  truncated: boolean;
  error: SourceError | null;
  stale: boolean;
}

export type SourceError = 'failed' | 'unreachable' | 'unauthorized' | 'timeout' | 'invalidResponse' | 'rateLimited';

export interface WorkItemReport {
  readAt: string | null;
  sourceKind: string;
  items: WorkItem[];
  error: SourceError | null;
  stale: boolean;
}

export interface SystemStatus {
  name: string;
  version: string;
  source: { kind: string; configured: boolean; readAt: string | null; error: SourceError | null; stale: boolean };
  forge: { kind: string; configured: boolean };
  approvals: ApprovalStatus;
  storage: StorageStatus;
  // The executors that asked for work: what they say of themselves, and whether they did so lately
  executors: ExecutorStatus[];
}

export type TaskKind = 'validatePullRequest' | 'mergePullRequest';

export interface DelegationInfo {
  id: string;
  subject: string;
  kind: TaskKind;
  repository: string;
  expiresAt: string;
  dailyQuota: number;
}

export interface ExecutorStatus {
  name: string;
  version: string;
  lastSeen: string;
  connected: boolean;
  kinds: TaskKind[];
  repositories: string[];
  delegations: DelegationInfo[];
}

// Where the approvals, the trail and the global stop are kept: "sqlite" survives a restart, "memory" does not.
export type StorageKind = 'sqlite' | 'memory';

export interface StorageStatus {
  kind: StorageKind;
}

export type ApprovalMode = 'disabled' | 'development' | 'openidconnect';

// Who may approve (the mode is "disabled", "development" or "openidconnect"), who the operator is (the identifier that counts, and the
// name to show), and whether the global stop is engaged.
export interface ApprovalStatus {
  mode: ApprovalMode;
  enabled: boolean;
  operator: string | null;
  operatorName: string | null;
  // Only an operator is offered approving and the global stop
  isOperator: boolean;
  killSwitchEngaged: boolean;
  // False when the stop is engaged but the file could not take it: it holds until a restart, which would release it
  killSwitchSaved: boolean;
  // Approving asks for the code of the authenticator app, which the executor checks (not this server)
  requiresSecondFactor: boolean;
}

// Who the session is, whatever it may do (GET /auth/me, only when people sign in): canRead false is an account the API refuses.
export interface Session {
  name: string | null;
  canRead: boolean;
  isOperator: boolean;
}

// Where an approval is in its life: a dry run spends it, so a real run needs a new approval.
export type ApprovalState = 'waiting' | 'taken' | 'dryRunDone' | 'refused' | 'expired';

// A decision of the operator, recorded: these steps on this pull request at this commit. It makes a batch of tasks that the executor takes.
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

export type TaskResult = 'dryRun' | 'refused' | 'skipped';

// Why the executor refused a batch: a code, which the interface words.
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
  executor: string | null;
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
  | 'approvalsDisabled'
  | 'notSignedIn'
  | 'notAnOperator'
  | 'notAPerson'
  | 'killSwitchEngaged'
  | 'unknownWorkItem'
  | 'pullRequestsNotRead'
  | 'nothingTicked'
  | 'planChanged'
  | 'hostNotAllowed'
  | 'originNotAllowed'
  | 'stepNotOffered'
  | 'stepNotGranted'
  | 'stepNotSupported'
  | 'mergeNeedsValidation'
  | 'noPullRequests'
  | 'alreadyApproved'
  | 'secondFactorRequired'
  | 'storeFull'
  | 'auditUnavailable'
  | 'tooManyRequests';

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
  // null: not read (no forge connected, or none to read); pullRequestsError says whether reading them failed
  pullRequests: PullRequest[] | null;
  pullRequestsError: SourceError | null;
}

export type SourceError = 'failed' | 'unreachable' | 'unauthorized' | 'timeout' | 'invalidResponse';

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
}

// The resources of the Orchestrarr API (GET /api/v1/...). Enums are camelCase strings.

export type Severity = 'info' | 'warning' | 'danger';

export type FindingKind =
  | 'dependencyAlerts'
  | 'codeScanningAlerts'
  | 'secretScanningAlerts'
  | 'openDependencyPullRequests'
  | 'failingCi'
  | 'outdatedImage';

export interface Finding {
  kind: FindingKind;
  severity: Severity;
  count: number;
  summary: string;
}

export type StepKind = 'validate' | 'merge' | 'deploy';

export interface PlanStep {
  kind: StepKind;
  label: string;
  environmentId: string | null;
  ticked: boolean;
  available: boolean;
  reason: string | null;
}

export interface Placement {
  environmentId: string;
  environmentName: string;
  namespace: string | null;
  workloadName: string;
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
}

export interface WorkItemReport {
  readAt: string | null;
  sourceKind: string;
  items: WorkItem[];
  error: string | null;
  stale: boolean;
}

export interface SystemStatus {
  name: string;
  version: string;
  source: { kind: string; configured: boolean; readAt: string | null; error: string | null; stale: boolean };
}

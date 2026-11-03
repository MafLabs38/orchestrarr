import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';

import type {
  ApprovalStatus,
  Finding,
  PlanStep,
  PullRequest,
  PullRequestsRead,
  SystemStatus,
  WorkItem,
  WorkItemReport,
} from 'typings/api';

// The providers are a wrapper, so that a rerender keeps them.
export function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const Providers = ({ children }: Readonly<{ children: ReactNode }>) => (
    <QueryClientProvider client={client}>
      <MemoryRouter>{children}</MemoryRouter>
    </QueryClientProvider>
  );
  return render(ui, { wrapper: Providers });
}

// Answers each API path from a table; an unknown path is a 404.
export function stubApi(routes: Record<string, unknown>) {
  const fetchMock = vi.fn((input: string) => {
    const path = String(input);
    if (path in routes) {
      return Promise.resolve(new Response(JSON.stringify(routes[path]), { status: 200 }));
    }
    return Promise.resolve(new Response('{}', { status: 404, statusText: 'Not Found' }));
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

// A fetch that never answers: the screen is still waiting.
export function stubPendingApi() {
  vi.stubGlobal(
    'fetch',
    vi.fn(() => new Promise<Response>(() => undefined)),
  );
}

export const finding: Finding = {
  kind: 'dependencyAlerts',
  severity: 'danger',
  count: 1,
  alerts: { critical: 1, high: 0, medium: 0, low: 0 },
  image: null,
};

export function step(overrides: Partial<PlanStep> = {}): PlanStep {
  return {
    kind: 'validate',
    environmentId: null,
    environmentName: null,
    ticked: true,
    available: true,
    granted: false,
    reason: null,
    ...overrides,
  };
}

export const workItem: WorkItem = {
  id: 'repository:example/shop',
  title: 'example/shop',
  repositoryFullName: 'example/shop',
  appIds: ['shop'],
  severity: 'danger',
  findings: [finding],
  plan: [step()],
  placements: [],
  pullRequests: { status: 'notNeeded', items: [], truncated: false, error: null, stale: false },
};

export function report(overrides: Partial<WorkItemReport> = {}): WorkItemReport {
  return {
    readAt: '2026-11-01T08:00:00Z',
    sourceKind: 'sample',
    items: [workItem],
    error: null,
    stale: false,
    ...overrides,
  };
}

export function approvalStatus(overrides: Partial<ApprovalStatus> = {}): ApprovalStatus {
  return { mode: 'development', enabled: true, operator: 'dev-operator', killSwitchEngaged: false, ...overrides };
}

export function status(
  overrides: Partial<SystemStatus['source']> = {},
  approvals: Partial<ApprovalStatus> = {},
): SystemStatus {
  return {
    name: 'Orchestrarr',
    version: '0.0.0-dev',
    source: {
      kind: 'sample',
      configured: true,
      readAt: '2026-11-01T08:00:00Z',
      error: null,
      stale: false,
      ...overrides,
    },
    forge: { kind: 'github', configured: true },
    approvals: approvalStatus(approvals),
  };
}

export function pullRequest(number: number, overrides: Partial<PullRequest> = {}): PullRequest {
  return {
    number,
    title: `Bump a from 1.0.0 to 1.0.${number}`,
    url: `https://forge.example/o/r/pull/${number}`,
    author: 'dependabot[bot]',
    headBranch: 'bot/a',
    draft: false,
    checks: 'passing',
    merge: 'clean',
    labels: [],
    changes: [{ package: 'a', from: '1.0.0', to: `1.0.${number}`, kind: 'patch' }],
    createdAt: '2026-11-01T09:38:16Z',
    headSha: 'abcdef0123456789abcdef0123456789abcdef01',
    readyToMerge: true,
    ...overrides,
  };
}

export function pullRequestsRead(overrides: Partial<PullRequestsRead> = {}): PullRequestsRead {
  return { status: 'read', items: [pullRequest(1)], truncated: false, error: null, stale: false, ...overrides };
}

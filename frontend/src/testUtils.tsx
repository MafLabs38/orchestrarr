import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement, ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';

import type { ApprovalStatus, ProblemRow, ProblemsReport, SystemStatus } from 'typings/api';

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

export function problem(overrides: Partial<ProblemRow> = {}): ProblemRow {
  return {
    id: 'pr:example/shop#2',
    workItemId: 'repository:example/shop',
    application: 'example/shop',
    kind: 'dependencyUpdate',
    description: 'express 4.16.0 → 4.21.2',
    severity: null,
    count: null,
    updateClass: 'minor',
    checks: 'passing',
    url: 'https://forge.example/example/shop/pull/2',
    pullRequest: 2,
    headSha: 'a'.repeat(40),
    goesUpTo: 'production',
    why: 'none',
    canStopBeforeProduction: true,
    ...overrides,
  };
}

export function problemsReport(
  rows: ProblemRow[] = [problem()],
  overrides: Partial<ProblemsReport> = {},
): ProblemsReport {
  return {
    readAt: '2026-11-01T08:00:00Z',
    sourceKind: 'sample',
    stale: false,
    error: null,
    descriptionsVersion: 'abc123',
    rows,
    ...overrides,
  };
}

export function approvalStatus(overrides: Partial<ApprovalStatus> = {}): ApprovalStatus {
  return {
    operator: 'test-operator',
    operatorName: 'test-operator',
    isOperator: true,
    killSwitchEngaged: false,
    killSwitchSaved: true,
    requiresSecondFactor: false,
    ...overrides,
  };
}

// A person who is signed in and may only read
export const readerOnly: Partial<ApprovalStatus> = { operator: 'reader-1', operatorName: 'reader', isOperator: false };

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
    storage: { kind: 'memory' },
    executors: [],
  };
}

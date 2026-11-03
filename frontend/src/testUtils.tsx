import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render } from '@testing-library/react';
import type { ReactElement } from 'react';
import { MemoryRouter } from 'react-router-dom';

import type { SystemStatus, WorkItem, WorkItemReport } from 'typings/api';

export function renderWithProviders(ui: ReactElement) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>{ui}</MemoryRouter>
    </QueryClientProvider>,
  );
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

export const workItem: WorkItem = {
  id: 'repository:example/shop',
  title: 'example/shop',
  repositoryFullName: 'example/shop',
  appIds: ['shop'],
  severity: 'danger',
  findings: [{ kind: 'dependencyAlerts', severity: 'danger', count: 1, summary: '1 dependency alert (1 critical)' }],
  plan: [
    {
      kind: 'validate',
      label: 'Build and test the change',
      environmentId: null,
      ticked: true,
      available: true,
      reason: null,
    },
  ],
  placements: [],
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

export function status(overrides: Partial<SystemStatus['source']> = {}): SystemStatus {
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
  };
}

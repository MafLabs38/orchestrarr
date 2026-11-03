import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';
import type { Approval } from 'typings/api';

import Queue from './Queue';

const approval: Approval = {
  id: 'a',
  workItemId: 'repository:example/shop',
  repository: 'example/shop',
  pullRequest: 7,
  headSha: 'cccccccccccccccccccccccccccccccccccccccc',
  steps: ['validate', 'mergePullRequests'],
  approver: 'dev-operator',
  approvedAt: '2026-11-03T10:00:00Z',
};

const approvalsPath = '/api/v1/approvals';
const statusPath = '/api/v1/system/status';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Queue', () => {
  it('lists what was approved, bound to its pull request and its commit', async () => {
    stubApi({ [approvalsPath]: [approval], [statusPath]: status() });

    renderWithProviders(<Queue />);

    expect(await screen.findByText('example/shop')).toBeInTheDocument();
    expect(screen.getByText('#7')).toBeInTheDocument();
    expect(screen.getByText('commit ccccccc')).toBeInTheDocument();
    expect(screen.getByText('Build and test the change · Merge the pull requests that pass')).toBeInTheDocument();
    expect(screen.getByText(/Approved by dev-operator at/)).toBeInTheDocument();
  });

  it('says that each approval waits, and that no launcher exists yet', async () => {
    stubApi({ [approvalsPath]: [approval], [statusPath]: status() });

    renderWithProviders(<Queue />);

    expect(await screen.findByText('Waiting for a launcher')).toBeInTheDocument();
    expect(screen.getByText(/No launcher exists yet/)).toBeInTheDocument();
  });

  it('says the queue is empty, and what to do', async () => {
    stubApi({ [approvalsPath]: [], [statusPath]: status() });

    renderWithProviders(<Queue />);

    expect(await screen.findByText(/Nothing is waiting/)).toBeInTheDocument();
    expect(screen.queryByText(/No launcher exists yet/)).not.toBeInTheDocument();
  });

  it('says when the queue cannot be read', async () => {
    stubApi({ [statusPath]: status() });

    renderWithProviders(<Queue />);

    expect(await screen.findByRole('alert')).toHaveTextContent('Cannot read the queue');
  });

  it('warns when the global stop is engaged', async () => {
    stubApi({ [approvalsPath]: [], [statusPath]: status({}, { killSwitchEngaged: true }) });

    renderWithProviders(<Queue />);

    expect(await screen.findByRole('alert')).toHaveTextContent('The global stop is engaged');
  });
});

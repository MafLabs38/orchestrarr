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
  state: 'waiting',
  batchId: null,
  endedAt: null,
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

  it('says where each approval is, with the word and not only a colour', async () => {
    const states = ['waiting', 'taken', 'dryRunDone', 'refused', 'expired'] as const;
    stubApi({
      [approvalsPath]: states.map((state, index) => ({ ...approval, id: `a${index}`, pullRequest: index + 1, state })),
      [statusPath]: status(),
    });

    renderWithProviders(<Queue />);

    expect(await screen.findByText('Waiting for the executor')).toBeInTheDocument();
    expect(screen.getByText('With the executor')).toBeInTheDocument();
    expect(screen.getByText('Run ended: see the run')).toBeInTheDocument();
    expect(screen.getByText('Refused by the executor')).toBeInTheDocument();
    expect(screen.getByText('Expired: approve again')).toBeInTheDocument();
  });

  it('links an approval to its run', async () => {
    stubApi({ [approvalsPath]: [{ ...approval, batchId: 'batch-1' }], [statusPath]: status() });

    renderWithProviders(<Queue />);

    expect(await screen.findByRole('link', { name: 'Open' })).toHaveAttribute('href', '/runs/batch-1');
  });

  it('says when no executor is connected, and says nothing when one is', async () => {
    stubApi({ [approvalsPath]: [approval], [statusPath]: status() });
    const { unmount } = renderWithProviders(<Queue />);
    expect(await screen.findByText(/No executor is connected/)).toBeInTheDocument();
    unmount();

    const connected = {
      ...status(),
      executors: [
        {
          name: 'executor-1',
          version: '1',
          lastSeen: '2026-11-03T10:00:00Z',
          connected: true,
          kinds: [],
          repositories: [],
          delegations: [],
          secondFactorRequired: true,
        },
      ],
    };
    stubApi({ [approvalsPath]: [approval], [statusPath]: connected });
    renderWithProviders(<Queue />);
    await screen.findByText('Waiting for the executor');
    expect(screen.queryByText(/No executor is connected/)).not.toBeInTheDocument();
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

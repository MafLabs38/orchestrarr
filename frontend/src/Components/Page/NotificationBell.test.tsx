import { screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { renderWithProviders, status, stubApi } from 'testUtils';

import NotificationBell from './NotificationBell';

afterEach(() => {
  vi.unstubAllGlobals();
});

const runner = {
  name: 'runner-1',
  version: '1',
  lastSeen: '2026-11-11T12:00:00Z',
  connected: true,
  kinds: ['validatePullRequest' as const],
  repositories: ['example/*'],
  delegations: [],
  secondFactorRequired: true,
};

// The status of a host with one runner connected, unless the test says otherwise
const statusWith = (approvals: Parameters<typeof status>[1] = {}, runners = [runner]) => ({
  ...status({}, { requiresSecondFactor: true, ...approvals }),
  runners,
});

describe('NotificationBell', () => {
  it('says nothing needs attention when everything holds, and shows no count', async () => {
    stubApi({ '/api/v1/system/status': statusWith() });
    renderWithProviders(<NotificationBell />);

    const bell = await screen.findByRole('button', { name: 'Notifications (0)' });
    await userEvent.click(bell);

    expect(screen.getByText('Nothing needs your attention.')).toBeInTheDocument();
  });

  it('marks the bell with an exclamation mark when the authenticator code is off, and says it when opened', async () => {
    stubApi({ '/api/v1/system/status': statusWith({ requiresSecondFactor: false }) });
    renderWithProviders(<NotificationBell />);

    const bell = await screen.findByRole('button', { name: 'Notifications (1)' });
    expect(bell).toHaveTextContent('!');
    await userEvent.click(bell);

    expect(screen.getByText('The authenticator code is off')).toBeInTheDocument();
    expect(screen.getByText(/Approvals carry no second proof/)).toBeInTheDocument();
    expect(screen.getByLabelText('Important')).toBeInTheDocument();
  });

  it('counts what does not stop the work without an exclamation mark and closes on Escape', async () => {
    stubApi({ '/api/v1/system/status': statusWith({}, []) });
    renderWithProviders(<NotificationBell />);

    const bell = await screen.findByRole('button', { name: 'Notifications (1)' });
    expect(bell).toHaveTextContent('1');
    await userEvent.click(bell);
    expect(screen.getByText('No Runner is connected')).toBeInTheDocument();

    await userEvent.keyboard('{Escape}');

    expect(screen.queryByText('No Runner is connected')).not.toBeInTheDocument();
  });

  it('says that the global stop is engaged as an important notification', async () => {
    stubApi({ '/api/v1/system/status': statusWith({ killSwitchEngaged: true }) });
    renderWithProviders(<NotificationBell />);

    await userEvent.click(await screen.findByRole('button', { name: 'Notifications (1)' }));

    expect(screen.getByText('The global stop is engaged')).toBeInTheDocument();
  });

  it('tells what is wrong with the code of the description: a repository that is not read, an environment the fleet does not know, a failed reading', async () => {
    stubApi({
      '/api/v1/system/status': {
        ...statusWith(),
        code: {
          problems: [{ repository: 'example/infra', reason: 'not found or no access' }],
          warnings: [{ subject: 'prod', reason: 'no workload of the fleet is in this environment' }],
          error: 'HttpRequestException',
        },
      },
    });
    renderWithProviders(<NotificationBell />);

    await userEvent.click(await screen.findByRole('button', { name: 'Notifications (3)' }));

    expect(screen.getByText('A repository of the description is not read: example/infra')).toBeInTheDocument();
    expect(screen.getByText(/not found or no access/)).toBeInTheDocument();
    expect(screen.getByText("The description names 'prod'")).toBeInTheDocument();
    expect(screen.getByText('The code of the description could not be read')).toBeInTheDocument();
  });
});

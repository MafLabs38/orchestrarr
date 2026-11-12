import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders, status, stubApi } from 'testUtils';
import type { StorageKind } from 'typings/api';

import Status from './Status';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('Status', () => {
  it('shows the source, the time of the last read and the version', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Sample data')).toBeInTheDocument();
    const lastRead = screen.getByText('Last read').nextElementSibling;
    expect(lastRead).toHaveTextContent(/\d{1,2}.\d{2}.\d{2}/);
    expect(lastRead).not.toHaveTextContent('never');
    expect(screen.getByText('0.0.0-dev')).toBeInTheDocument();
  });

  it('says whether the forge is connected', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('GitHub · Connected')).toBeInTheDocument();
  });

  it('says a sample forge is sample data', async () => {
    stubApi({ '/api/v1/system/status': { ...status(), forge: { kind: 'sample', configured: true } } });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Sample data · Connected')).toBeInTheDocument();
  });

  it('says when no forge is connected and what that means', async () => {
    stubApi({ '/api/v1/system/status': { ...status(), forge: { kind: 'none', configured: false } } });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Not connected: pull requests are not read')).toBeInTheDocument();
  });

  it('says never when nothing was read yet', async () => {
    stubApi({ '/api/v1/system/status': status({ readAt: null }) });

    renderWithProviders(<Status />);

    expect(await screen.findByText('never')).toBeInTheDocument();
  });

  it('words why the source cannot be read', async () => {
    stubApi({ '/api/v1/system/status': status({ kind: 'portarr', error: 'unauthorized' }) });

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Cannot read the fleet: The source refused the credentials, or asks to sign in',
    );
  });

  it('says who approves, and that the global stop is off', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Signed in: test-operator')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Engage the global stop' })).toBeEnabled();
  });

  it('says who is signed in', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { operator: 'a1b2', operatorName: 'alice' }),
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Signed in: alice')).toBeInTheDocument();
  });

  it('offers no global stop to somebody who is signed in but is not an operator, and says why', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { operator: 'bob-2', operatorName: 'bob', isOperator: false }),
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Signed in: bob')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /global stop/ })).not.toBeInTheDocument();
    expect(screen.getByText('Only an operator can use the global stop.')).toBeInTheDocument();
  });

  it('engages the global stop with a PUT', async () => {
    const fetchMock = vi.fn((_input: string, init?: RequestInit) =>
      Promise.resolve(
        new Response(JSON.stringify(init?.method === 'PUT' ? { killSwitchEngaged: true } : status()), { status: 200 }),
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<Status />);

    await userEvent.click(await screen.findByRole('button', { name: 'Engage the global stop' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/v1/safety/kill-switch',
        expect.objectContaining({ method: 'PUT', body: '{"engaged":true}' }),
      ),
    );
  });

  it('shows an engaged global stop as an alert and offers to release it', async () => {
    stubApi({ '/api/v1/system/status': status({}, { killSwitchEngaged: true }) });

    renderWithProviders(<Status />);

    expect(await screen.findByRole('alert')).toHaveTextContent('The global stop is engaged');
    expect(screen.getByRole('button', { name: 'Release the global stop' })).toBeInTheDocument();
  });

  it('says where the approvals and the trail are kept', async () => {
    stubApi({ '/api/v1/system/status': { ...status(), storage: { kind: 'sqlite' } } });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Saved in a file: kept across restarts')).toBeInTheDocument();
  });

  it('warns that nothing is kept across a restart when everything is in memory', async () => {
    stubApi({ '/api/v1/system/status': status() });

    renderWithProviders(<Status />);

    expect(await screen.findByText('In memory: lost at every restart')).toBeInTheDocument();
  });

  it('shows a kind of storage it does not know as the server names it', async () => {
    stubApi({ '/api/v1/system/status': { ...status(), storage: { kind: 'postgres' as StorageKind } } });

    renderWithProviders(<Status />);

    expect(await screen.findByText('postgres')).toBeInTheDocument();
  });

  it('warns that an engaged stop that could not be saved would not survive a restart', async () => {
    stubApi({
      '/api/v1/system/status': {
        ...status({}, { killSwitchEngaged: true, killSwitchSaved: false }),
        storage: { kind: 'sqlite' },
      },
    });

    renderWithProviders(<Status />);

    expect(
      await screen.findByText(/could not be saved or read: do not count on it across a restart/),
    ).toBeInTheDocument();
  });

  it('does not warn about saving when the stop is saved', async () => {
    stubApi({ '/api/v1/system/status': status({}, { killSwitchEngaged: true }) });

    renderWithProviders(<Status />);

    await screen.findByRole('button', { name: 'Release the global stop' });
    expect(screen.queryByText(/do not count on it across a restart/)).not.toBeInTheDocument();
  });

  it.each([
    ['auditUnavailable', /cannot be written to the audit trail/],
    ['killSwitchUnavailable', /stays engaged/],
    ['somethingNew', /could not be changed/],
  ])('says why a release was refused (%s) and the stop stays engaged', async (code, text) => {
    vi.stubGlobal(
      'fetch',
      vi.fn((_input: string, init?: RequestInit) =>
        Promise.resolve(
          init?.method === 'PUT'
            ? new Response(JSON.stringify({ error: code }), { status: 503 })
            : new Response(JSON.stringify(status({}, { killSwitchEngaged: true })), { status: 200 }),
        ),
      ),
    );
    renderWithProviders(<Status />);

    await userEvent.click(await screen.findByRole('button', { name: 'Release the global stop' }));

    expect(await screen.findByText(text)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Release the global stop' })).toBeInTheDocument();
  });

  it('says when the global stop could not be changed', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn((_input: string, init?: RequestInit) =>
        Promise.resolve(
          init?.method === 'PUT'
            ? new Response('{}', { status: 500 })
            : new Response(JSON.stringify(status()), { status: 200 }),
        ),
      ),
    );
    renderWithProviders(<Status />);

    await userEvent.click(await screen.findByRole('button', { name: 'Engage the global stop' }));

    expect(await screen.findByText('The global stop could not be changed.')).toBeInTheDocument();
  });

  it('releases the global stop with a PUT when it is engaged', async () => {
    const fetchMock = vi.fn((_input: string, init?: RequestInit) =>
      Promise.resolve(
        new Response(
          JSON.stringify(
            init?.method === 'PUT' ? { killSwitchEngaged: false } : status({}, { killSwitchEngaged: true }),
          ),
          { status: 200 },
        ),
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<Status />);

    await userEvent.click(await screen.findByRole('button', { name: 'Release the global stop' }));

    await waitFor(() =>
      expect(fetchMock).toHaveBeenCalledWith(
        '/api/v1/safety/kill-switch',
        expect.objectContaining({ method: 'PUT', body: '{"engaged":false}' }),
      ),
    );
  });

  it('does not warn about saving when everything is in memory by choice', async () => {
    stubApi({ '/api/v1/system/status': status({}, { killSwitchEngaged: true, killSwitchSaved: false }) });

    renderWithProviders(<Status />);

    await screen.findByRole('button', { name: 'Release the global stop' });
    expect(screen.queryByText(/do not count on it across a restart/)).not.toBeInTheDocument();
  });
  it('says when no runner has asked for work, and what it accepts and holds when one has', async () => {
    stubApi({ '/api/v1/system/status': status() });
    const { unmount } = renderWithProviders(<Status />);
    expect(await screen.findByText(/No runner has asked for work yet/)).toBeInTheDocument();
    unmount();

    stubApi({
      '/api/v1/system/status': {
        ...status({}, { requiresSecondFactor: true }),
        runners: [
          {
            name: 'runner-1',
            version: '1.2.3',
            lastSeen: '2026-11-09T12:00:00Z',
            connected: true,
            kinds: ['validatePullRequest', 'mergePullRequest'],
            repositories: ['example/*'],
            delegations: [
              {
                id: 'd1',
                subject: 'alice',
                kind: 'mergePullRequest',
                repository: 'example/*',
                expiresAt: '2026-12-09T12:00:00Z',
                dailyQuota: 5,
              },
            ],
            secondFactorRequired: true,
          },
        ],
      },
    });
    renderWithProviders(<Status />);

    expect(await screen.findByText('runner-1')).toBeInTheDocument();
    expect(screen.getByText('Connected')).toBeInTheDocument();
    expect(screen.getByText('Accepts: Validate, Merge')).toBeInTheDocument();
    expect(screen.getByText('Repositories: example/*')).toBeInTheDocument();
    expect(screen.getByText(/Merge on example\/\* until 2026-12-09, 5 a day/)).toBeInTheDocument();
    expect(screen.getByText('A code from your authenticator app, checked by the runner')).toBeInTheDocument();
  });

  it('says that a runner does not check the code of the authenticator (development)', async () => {
    stubApi({
      '/api/v1/system/status': {
        ...status(),
        runners: [
          {
            name: 'runner-1',
            version: '1',
            lastSeen: '2026-11-09T12:00:00Z',
            connected: true,
            kinds: [],
            repositories: [],
            delegations: [],
            secondFactorRequired: false,
          },
        ],
      },
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('does not check the code (development)')).toBeInTheDocument();
  });

  it('says that a runner not seen for a while is not connected', async () => {
    stubApi({
      '/api/v1/system/status': {
        ...status(),
        runners: [
          {
            name: 'runner-1',
            version: '1',
            lastSeen: '2026-11-09T12:00:00Z',
            connected: false,
            kinds: [],
            repositories: [],
            delegations: [],
            secondFactorRequired: true,
          },
        ],
      },
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Not seen for a while')).toBeInTheDocument();
    expect(screen.getAllByText(/none \(it refuses everything\)/)).toHaveLength(2);
  });
});

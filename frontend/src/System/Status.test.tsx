import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders, status, stubApi } from 'testUtils';

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

    expect(await screen.findByText('Sandbox operator: dev-operator')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Engage the global stop' })).toBeEnabled();
  });

  it('says who is signed in when people sign in through the identity provider', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { mode: 'openidconnect', operator: 'a1b2', operatorName: 'alice' }),
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Signed in: alice')).toBeInTheDocument();
  });

  it('says approving is turned off, and offers no global stop to nobody', async () => {
    stubApi({
      '/api/v1/system/status': status(
        {},
        { enabled: false, mode: 'disabled', operator: null, operatorName: null, isOperator: false },
      ),
    });

    renderWithProviders(<Status />);

    expect(await screen.findByText('Turned off')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /global stop/ })).not.toBeInTheDocument();
    expect(screen.getByText('Only a signed-in operator can use the global stop.')).toBeInTheDocument();
  });

  it('offers no global stop to somebody who is signed in but is not an operator, and says why', async () => {
    stubApi({
      '/api/v1/system/status': status(
        {},
        { mode: 'openidconnect', operator: 'bob-2', operatorName: 'bob', isOperator: false },
      ),
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
});

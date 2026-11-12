import { screen, waitFor } from '@testing-library/react';

import { renderWithProviders, status, stubApi, stubPendingApi } from 'testUtils';

import Page from './Page';

const session = { subject: 's-1', name: 'alice', role: 'Operator', permissions: ['fleet.read', 'approvals.create'] };

describe('Page', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows nothing of the fleet while it does not know who is asking', () => {
    stubPendingApi();

    renderWithProviders(<Page>the decisions</Page>);

    expect(screen.queryByText('the decisions')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign in' })).not.toBeInTheDocument();
  });

  it('marks the bell of the header when the authenticator code is off, without a banner that takes the width of the page', async () => {
    stubApi({ '/api/v1/auth/me': session, '/api/v1/system/status': status({}, { requiresSecondFactor: false }) });

    renderWithProviders(<Page>the decisions</Page>);

    const bell = await screen.findAllByRole('button', { name: /Notifications \(/ });
    expect(bell).toHaveLength(1);
    await screen.findByText('the decisions');
    expect(screen.queryByText(/The authenticator code is off/)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: /Notifications \([1-9]/ })).toHaveTextContent('!'));
  });

  it('does not say the code is off when approving asks for it', async () => {
    const fetchMock = stubApi({
      '/api/v1/auth/me': session,
      '/api/v1/system/status': status({}, { requiresSecondFactor: true }),
    });

    renderWithProviders(<Page>the decisions</Page>);

    await waitFor(() =>
      expect(fetchMock.mock.calls.some(([path]) => String(path) === '/api/v1/system/status')).toBe(true),
    );
    await screen.findByText('the decisions');
    expect(screen.queryByText(/The authenticator code is off/)).not.toBeInTheDocument();
    expect(screen.queryByText('!')).not.toBeInTheDocument();
  });

  it('shows the sign-in and none of the pages when nobody is signed in', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{}', { status: 401 }))),
    );

    renderWithProviders(<Page>the decisions</Page>);

    expect(await screen.findByRole('button', { name: 'Sign in' })).toBeInTheDocument();
    expect(screen.queryByText('the decisions')).not.toBeInTheDocument();
  });

  it('shows the pages and who is signed in once there is a session, with no link to the accounts for an operator', async () => {
    stubApi({ '/api/v1/auth/me': session });

    renderWithProviders(<Page>the decisions</Page>);

    expect(await screen.findByText('the decisions')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'alice (Operator)' })).toBeInTheDocument();
    expect(screen.queryByRole('link', { name: 'Accounts' })).not.toBeInTheDocument();
  });

  it('offers the accounts to the administrator', async () => {
    stubApi({ '/api/v1/auth/me': { ...session, role: 'Admin', permissions: ['fleet.read', 'accounts.manage'] } });

    renderWithProviders(<Page>the decisions</Page>);

    expect(await screen.findByRole('link', { name: 'Accounts' })).toBeInTheDocument();
  });

  it('says the server cannot be reached rather than showing a sign-in that cannot work', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response('{}', { status: 502 }))),
    );

    renderWithProviders(<Page>the decisions</Page>);

    expect(await screen.findByRole('alert')).toHaveTextContent('cannot be reached');
  });
});

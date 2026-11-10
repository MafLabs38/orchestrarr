import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders } from 'testUtils';

import Options from './Options';

const admin = { subject: 's-1', name: 'root', role: 'Admin', permissions: ['fleet.read', 'options.manage'] };
const reader = { subject: 's-2', name: 'bob', role: 'Reader', permissions: ['fleet.read'] };
const id = 'approvals.require-second-factor';
const option = (changes: Record<string, unknown> = {}) => ({
  id,
  value: true,
  default: true,
  lowersSecurityWhenOff: true,
  canTurnOff: true,
  lockedReason: null,
  ...changes,
});

function stub(session: unknown, options: unknown[], refuse: Record<string, Response> = {}) {
  const fetchMock = vi.fn((input: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`;
    if (key in refuse) {
      return Promise.resolve(refuse[key]);
    }
    const bodies: Record<string, unknown> = { '/api/v1/auth/me': session, '/api/v1/options': options };
    return Promise.resolve(
      String(input) in bodies && (init?.method ?? 'GET') === 'GET'
        ? new Response(JSON.stringify(bodies[String(input)]), { status: 200 })
        : new Response(JSON.stringify(options), { status: 200 }),
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const put = (fetchMock: ReturnType<typeof stub>) =>
  fetchMock.mock.calls.find(([, init]) => (init as RequestInit | undefined)?.method === 'PUT');

describe('Options', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('shows the option and its state, with a button to turn it off for an administrator', async () => {
    stub(admin, [option()]);

    renderWithProviders(<Options />);

    expect(await screen.findByText('Authenticator code to approve')).toBeInTheDocument();
    expect(screen.getByText('On')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Turn off…' })).toBeInTheDocument();
  });

  it('asks for the password before turning it off, and sends it with the change', async () => {
    const fetchMock = stub(admin, [option()]);
    renderWithProviders(<Options />);

    await userEvent.click(await screen.findByRole('button', { name: 'Turn off…' }));
    const confirm = screen.getByRole('button', { name: 'Turn off the code' });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText('Your password'), 'the password');
    await userEvent.click(confirm);

    await waitFor(() => expect(put(fetchMock)).toBeDefined());
    expect(put(fetchMock)?.[0]).toBe(`/api/v1/options/${id}`);
    expect(JSON.parse(String((put(fetchMock)?.[1] as RequestInit).body))).toEqual({
      value: false,
      password: 'the password',
    });
  });

  it('says so when the password is wrong, and nothing else changes', async () => {
    stub(admin, [option()], {
      [`PUT /api/v1/options/${id}`]: new Response(JSON.stringify({ error: 'invalidCredentials' }), { status: 401 }),
    });
    renderWithProviders(<Options />);

    await userEvent.click(await screen.findByRole('button', { name: 'Turn off…' }));
    await userEvent.type(screen.getByLabelText('Your password'), 'nope');
    await userEvent.click(screen.getByRole('button', { name: 'Turn off the code' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('That password is not right');
    expect(screen.getByText('On')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Turn off the code' })).toBeInTheDocument();
  });

  it('closes the form and forgets the password when cancelled', async () => {
    stub(admin, [option()]);
    renderWithProviders(<Options />);

    await userEvent.click(await screen.findByRole('button', { name: 'Turn off…' }));
    await userEvent.type(screen.getByLabelText('Your password'), 'typed');
    await userEvent.click(screen.getByRole('button', { name: 'Cancel' }));

    expect(screen.queryByLabelText('Your password')).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Turn off…' }));
    expect(screen.getByLabelText('Your password')).toHaveValue('');
  });

  it('says that the host does not allow it when the server answers so', async () => {
    stub(admin, [option()], {
      [`PUT /api/v1/options/${id}`]: new Response(JSON.stringify({ error: 'lockedByHost' }), { status: 403 }),
    });
    renderWithProviders(<Options />);

    await userEvent.click(await screen.findByRole('button', { name: 'Turn off…' }));
    await userEvent.type(screen.getByLabelText('Your password'), 'the password');
    await userEvent.click(screen.getByRole('button', { name: 'Turn off the code' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('does not allow lowering security');
  });

  it('turns it back on without asking for anything', async () => {
    const fetchMock = stub(admin, [option({ value: false })]);
    renderWithProviders(<Options />);

    await userEvent.click(await screen.findByRole('button', { name: 'Turn on' }));

    await waitFor(() => expect(put(fetchMock)).toBeDefined());
    expect(JSON.parse(String((put(fetchMock)?.[1] as RequestInit).body))).toEqual({ value: true });
  });

  it('says why a locked host cannot be changed and offers no button', async () => {
    stub(admin, [option({ canTurnOff: false, lockedReason: 'host' })]);

    renderWithProviders(<Options />);

    expect(await screen.findByText(/does not allow lowering security/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Turn off…' })).not.toBeInTheDocument();
  });

  it('says that the configuration fixes it', async () => {
    stub(admin, [option({ value: false, canTurnOff: false, lockedReason: 'configuration' })]);

    renderWithProviders(<Options />);

    expect(await screen.findByText(/configuration of the host fixes this option/)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Turn on' })).not.toBeInTheDocument();
  });

  it('shows a reader the options and no button', async () => {
    stub(reader, [option()]);

    renderWithProviders(<Options />);

    expect(await screen.findByText('Authenticator code to approve')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Turn off…' })).not.toBeInTheDocument();
  });
});

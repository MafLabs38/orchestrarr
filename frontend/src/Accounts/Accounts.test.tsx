import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders } from 'testUtils';

import Accounts from './Accounts';

const me = { subject: 's-1', name: 'root', role: 'Admin', permissions: ['accounts.manage'] };
const accounts = [
  {
    id: '11111111-1111-1111-1111-111111111111',
    name: 'root',
    role: 'Admin',
    enabled: true,
    createdAt: '2026-11-10T09:00:00Z',
  },
  {
    id: '22222222-2222-2222-2222-222222222222',
    name: 'bob',
    role: 'Reader',
    enabled: true,
    createdAt: '2026-11-10T09:05:00Z',
  },
];
const roles = ['Admin', 'Operator', 'Reader'].map((name) => ({ name, description: name, permissions: [] }));

function stub(refuse: Record<string, Response> = {}) {
  const fetchMock = vi.fn((input: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`;
    if (key in refuse) {
      return Promise.resolve(refuse[key]);
    }
    const bodies: Record<string, unknown> = {
      '/api/v1/auth/me': me,
      '/api/v1/accounts': accounts,
      '/api/v1/roles': roles,
    };
    return Promise.resolve(
      String(input) in bodies && (init?.method ?? 'GET') === 'GET'
        ? new Response(JSON.stringify(bodies[String(input)]), { status: 200 })
        : new Response('{}', { status: 200 }),
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const sent = (fetchMock: ReturnType<typeof stub>, method: string, path: string) =>
  fetchMock.mock.calls.find(
    ([input, init]) => String(input) === path && (init as RequestInit | undefined)?.method === method,
  );

describe('Accounts', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('lists the accounts with their role and state, and marks the one signed in', async () => {
    stub();

    renderWithProviders(<Accounts />);

    const rows = await screen.findAllByRole('row');
    expect(within(rows[1]).getByText(/root/)).toBeInTheDocument();
    expect(within(rows[1]).getByText('(you)')).toBeInTheDocument();
    expect(within(rows[2]).getByLabelText('Role of bob')).toHaveValue('Reader');
  });

  it('changes a role at once', async () => {
    const fetchMock = stub();
    renderWithProviders(<Accounts />);

    await userEvent.selectOptions(await screen.findByLabelText('Role of bob'), 'Operator');

    await waitFor(() => expect(sent(fetchMock, 'PUT', `/api/v1/accounts/${accounts[1].id}`)).toBeDefined());
    expect(
      JSON.parse(String((sent(fetchMock, 'PUT', `/api/v1/accounts/${accounts[1].id}`)?.[1] as RequestInit).body)),
    ).toEqual({ role: 'Operator', enabled: true });
  });

  it('disables an account', async () => {
    const fetchMock = stub();
    renderWithProviders(<Accounts />);

    await userEvent.click(await screen.findByLabelText('bob is enabled'));

    await waitFor(() => expect(sent(fetchMock, 'PUT', `/api/v1/accounts/${accounts[1].id}`)).toBeDefined());
    expect(
      JSON.parse(String((sent(fetchMock, 'PUT', `/api/v1/accounts/${accounts[1].id}`)?.[1] as RequestInit).body)),
    ).toMatchObject({ enabled: false });
  });

  it('creates an account, and says when the name is taken', async () => {
    const fetchMock = stub({
      'POST /api/v1/accounts': new Response(JSON.stringify({ error: 'conflict' }), { status: 409 }),
    });
    renderWithProviders(<Accounts />);
    await screen.findByLabelText('Role of bob');

    await userEvent.type(screen.getByLabelText('Name'), 'carol');
    await userEvent.type(screen.getAllByLabelText('Password').at(-1) as HTMLElement, 'a long enough password');
    await userEvent.click(screen.getByRole('button', { name: 'Create the account' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('That name is taken');
    expect(JSON.parse(String((sent(fetchMock, 'POST', '/api/v1/accounts')?.[1] as RequestInit).body))).toEqual({
      name: 'carol',
      role: 'Reader',
      password: 'a long enough password',
    });
  });

  it('deletes an account only after a confirmation', async () => {
    const fetchMock = stub();
    const confirm = vi.fn().mockReturnValueOnce(false).mockReturnValueOnce(true);
    vi.stubGlobal('confirm', confirm);
    renderWithProviders(<Accounts />);
    const row = (await screen.findByLabelText('Role of bob')).closest('tr') as HTMLElement;

    await userEvent.click(within(row).getByRole('button', { name: 'Delete' }));
    expect(sent(fetchMock, 'DELETE', `/api/v1/accounts/${accounts[1].id}`)).toBeUndefined();
    await userEvent.click(within(row).getByRole('button', { name: 'Delete' }));

    await waitFor(() => expect(sent(fetchMock, 'DELETE', `/api/v1/accounts/${accounts[1].id}`)).toBeDefined());
    expect(confirm).toHaveBeenCalledTimes(2);
  });

  it('says so when the last administrator cannot be changed', async () => {
    stub({
      [`PUT /api/v1/accounts/${accounts[0].id}`]: new Response(JSON.stringify({ error: 'conflict' }), { status: 409 }),
    });
    renderWithProviders(<Accounts />);

    await userEvent.selectOptions(await screen.findByLabelText('Role of root'), 'Reader');

    expect(await screen.findByRole('alert')).toHaveTextContent('no administrator');
  });
});

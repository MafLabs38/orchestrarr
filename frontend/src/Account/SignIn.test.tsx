import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders } from 'testUtils';

import SignIn from './SignIn';

const session = { subject: 's-1', name: 'root', role: 'Admin', permissions: ['fleet.read'] };

function stubLogin(answer: Response) {
  const fetchMock = vi.fn(() => Promise.resolve(answer));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

async function fill(name: string, password: string) {
  await userEvent.type(screen.getByLabelText('Name'), name);
  await userEvent.type(screen.getByLabelText('Password'), password);
}

describe('SignIn', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('sends the name and the password with the header that only the interface sends', async () => {
    const fetchMock = stubLogin(new Response(JSON.stringify(session), { status: 200 }));
    renderWithProviders(<SignIn />);

    await fill(' root ', 'the first password');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    const [path, init] = (fetchMock.mock.calls as unknown as [string, RequestInit][])[0];
    expect(path).toBe('/api/v1/auth/login');
    expect(JSON.parse(String(init.body))).toEqual({ name: 'root', password: 'the first password' });
    expect(init.headers).toMatchObject({ 'X-Orchestrarr-Request': 'web' });
  });

  it('cannot be sent without a name and a password', () => {
    renderWithProviders(<SignIn />);

    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled();
  });

  it('says the name or the password is wrong without saying which', async () => {
    stubLogin(new Response(JSON.stringify({ error: 'invalidCredentials' }), { status: 401 }));
    renderWithProviders(<SignIn />);

    await fill('root', 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Wrong name or password.');
  });

  it('says to come back later when the name is locked', async () => {
    stubLogin(new Response(JSON.stringify({ error: 'locked' }), { status: 429 }));
    renderWithProviders(<SignIn />);

    await fill('root', 'wrong');
    await userEvent.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Too many failed attempts');
  });
});

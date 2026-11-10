import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders } from 'testUtils';

import SignedInUser from './SignedInUser';

afterEach(() => {
  vi.unstubAllGlobals();
});

const session = { subject: 's-1', name: 'alice', role: 'Operator', permissions: ['fleet.read'] };

describe('SignedInUser', () => {
  it('says who is signed in with their role, and links to their account', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(() => Promise.resolve(new Response(JSON.stringify(session), { status: 200 }))),
    );

    renderWithProviders(<SignedInUser />);

    const link = await screen.findByRole('link', { name: 'alice (Operator)' });
    expect(link).toHaveAttribute('href', '/account');
  });

  it('shows nothing while nobody is signed in', async () => {
    const fetchMock = vi.fn(() => Promise.resolve(new Response('{}', { status: 401 })));
    vi.stubGlobal('fetch', fetchMock);

    renderWithProviders(<SignedInUser />);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
  });

  it('signs out with a write that carries the header of the interface', async () => {
    const fetchMock = vi.fn((_input: string, init?: RequestInit) =>
      Promise.resolve(
        init?.method === 'POST'
          ? new Response(null, { status: 204 })
          : new Response(JSON.stringify(session), { status: 200 }),
      ),
    );
    vi.stubGlobal('fetch', fetchMock);
    renderWithProviders(<SignedInUser />);

    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }));

    await waitFor(() => {
      const call = (fetchMock.mock.calls as unknown as [string, RequestInit][]).find(
        ([path]) => path === '/api/v1/auth/logout',
      );
      expect(call?.[1].method).toBe('POST');
      expect(call?.[1].headers).toMatchObject({ 'X-Orchestrarr-Request': 'web' });
    });
  });
});

import { screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';

import { renderWithProviders, stubApi } from 'testUtils';

import Account from './Account';

const session = {
  subject: '0b1c2d3e-aaaa-bbbb-cccc-000000000001',
  name: 'alice',
  role: 'Operator',
  permissions: ['fleet.read', 'approvals.create'],
};

describe('Account', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('says who the person is and the identifier the authenticator is enrolled under', async () => {
    stubApi({ '/api/v1/auth/me': session });

    renderWithProviders(<Account />);

    expect(await screen.findByText(session.subject)).toBeInTheDocument();
    expect(screen.getByText('Operator')).toBeInTheDocument();
    expect(screen.getByText(/fleet.read, approvals.create/)).toBeInTheDocument();
  });

  it('does not send two passwords that differ', async () => {
    stubApi({ '/api/v1/auth/me': session });
    renderWithProviders(<Account />);
    await screen.findByText(session.subject);

    await userEvent.type(screen.getByLabelText('Current password'), 'the old password');
    await userEvent.type(screen.getByLabelText('New password'), 'a brand new password');
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'another one');

    expect(screen.getByText('The two passwords differ.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Change password' })).toBeDisabled();
  });

  it('changes the password, and says the other sessions ended', async () => {
    const fetchMock = stubApi({ '/api/v1/auth/me': session });
    fetchMock.mockImplementation((input: string) =>
      Promise.resolve(
        String(input) === '/api/v1/auth/password'
          ? new Response(null, { status: 204 })
          : new Response(JSON.stringify(session), { status: 200 }),
      ),
    );
    renderWithProviders(<Account />);
    await screen.findByText(session.subject);

    await userEvent.type(screen.getByLabelText('Current password'), 'the old password');
    await userEvent.type(screen.getByLabelText('New password'), 'a brand new password');
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'a brand new password');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));

    await waitFor(() => expect(screen.getByText(/other sessions were ended/)).toBeInTheDocument());
    const call = (fetchMock.mock.calls as unknown as [string, RequestInit][]).find(
      ([path]) => path === '/api/v1/auth/password',
    );
    expect(JSON.parse(String(call?.[1].body))).toEqual({
      currentPassword: 'the old password',
      newPassword: 'a brand new password',
    });
  });

  it('says the current password is wrong', async () => {
    const fetchMock = stubApi({ '/api/v1/auth/me': session });
    fetchMock.mockImplementation((input: string) =>
      Promise.resolve(
        String(input) === '/api/v1/auth/password'
          ? new Response(JSON.stringify({ error: 'invalidCredentials' }), { status: 401 })
          : new Response(JSON.stringify(session), { status: 200 }),
      ),
    );
    renderWithProviders(<Account />);
    await screen.findByText(session.subject);

    await userEvent.type(screen.getByLabelText('Current password'), 'not mine');
    await userEvent.type(screen.getByLabelText('New password'), 'a brand new password');
    await userEvent.type(screen.getByLabelText('Repeat the new password'), 'a brand new password');
    await userEvent.click(screen.getByRole('button', { name: 'Change password' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('The current password is wrong.');
  });
});

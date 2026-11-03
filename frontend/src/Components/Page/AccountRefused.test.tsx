import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import AccountRefused from './AccountRefused';
import SignedInUser from './SignedInUser';

afterEach(() => {
  vi.unstubAllGlobals();
});

// SignedInUser reads the same session: when it shows, the session has answered, and the absence of the alert means something
function renderBeside(session: unknown) {
  stubApi({ '/api/v1/system/status': status(), '/auth/me': session });
  renderWithProviders(
    <>
      <AccountRefused />
      <SignedInUser />
    </>,
  );
  return screen.findByRole('button', { name: 'Sign out' });
}

describe('AccountRefused', () => {
  it('says an account that the API refuses cannot use the application', async () => {
    await renderBeside({ name: 'carol', canRead: false, isOperator: false });

    expect(screen.getByRole('alert')).toHaveTextContent('This account cannot use Orchestrarr');
  });

  it.each([[{ name: 'bob', canRead: true, isOperator: false }], [{ name: 'alice', canRead: true, isOperator: true }]])(
    'says nothing to an account that can read',
    async (session) => {
      await renderBeside(session);

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    },
  );
});

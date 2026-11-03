import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import AccountRefused from './AccountRefused';
import SourceBadge from './SourceBadge';

afterEach(() => {
  vi.unstubAllGlobals();
});

function renderBeside(session: unknown) {
  stubApi({ '/api/v1/system/status': status(), '/auth/me': session });
  renderWithProviders(
    <>
      <AccountRefused />
      <SourceBadge />
    </>,
  );
  return screen.findByText('Sample data · Connected');
}

describe('AccountRefused', () => {
  it('says an account that the API refuses cannot use the application', async () => {
    await renderBeside({ name: 'carol', canRead: false, isOperator: false });

    expect(await screen.findByRole('alert')).toHaveTextContent('This account cannot use Orchestrarr');
  });

  it.each([[{ name: 'bob', canRead: true, isOperator: false }], [{ name: 'alice', canRead: true, isOperator: true }]])(
    'says nothing to an account that can read',
    async (session) => {
      await renderBeside(session);

      expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    },
  );
});

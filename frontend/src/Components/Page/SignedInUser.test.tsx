import { screen } from '@testing-library/react';

import { renderWithProviders, status, stubApi } from 'testUtils';

import SignedInUser from './SignedInUser';
import SourceBadge from './SourceBadge';

afterEach(() => {
  vi.unstubAllGlobals();
});

// The status is read once for both: when the badge shows, the user has had its chance to show too
function renderBeside() {
  renderWithProviders(
    <>
      <SignedInUser />
      <SourceBadge />
    </>,
  );
  return screen.findByText('Sample data · Connected');
}

describe('SignedInUser', () => {
  it('says who is signed in by the name to show, and signs out with a form', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { mode: 'openidconnect', operator: 'a1b2', operatorName: 'alice' }),
    });

    await renderBeside();

    expect(await screen.findByText('Signed in as alice')).toBeInTheDocument();
    const button = screen.getByRole('button', { name: 'Sign out' });
    expect(button).toHaveAttribute('type', 'submit');
    const form = button.closest('form');
    expect(form).toHaveAttribute('method', 'post');
    expect(form).toHaveAttribute('action', '/auth/logout');
  });

  it('falls back on the identifier when there is no name', async () => {
    stubApi({
      '/api/v1/system/status': status({}, { mode: 'openidconnect', operator: 'a1b2', operatorName: null }),
    });

    await renderBeside();

    expect(await screen.findByText('Signed in as a1b2')).toBeInTheDocument();
  });

  it.each([
    [{ mode: 'development' as const }],
    [{ mode: 'openidconnect' as const, operator: null, operatorName: null, isOperator: false }],
  ])('shows nothing where nobody has anything to sign out of', async (approvals) => {
    stubApi({ '/api/v1/system/status': status({}, approvals) });

    await renderBeside();

    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
  });
});
